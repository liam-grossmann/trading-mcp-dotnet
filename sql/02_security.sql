/* =====================================================================
   Least-privilege user for the MCP server.
   Run AFTER 01_schema.sql, connected to the trading database as an admin.
   (Azure SQL: a contained database user with a password - no server login needed.)
   ===================================================================== */

/* Safety check: never create objects in a system database. */
IF DB_NAME() IN (N'master', N'tempdb', N'model', N'msdb')
BEGIN
    RAISERROR(N'You are connected to a system database. Switch to the TradingDemo database first, then run this script again.', 16, 1);
    SET NOEXEC ON;   -- skip everything below
END
GO

CREATE USER mcp_reader WITH PASSWORD = '<<replace-with-a-long-random-password>>';

-- LAYER 1 (video 2, first half): read everything in the trading schema (tables and views).
-- 03_procedures.sql later REVOKES this and leaves only EXECUTE on the mcp procedures (layer 2).
GRANT SELECT ON SCHEMA::trading TO mcp_reader;

-- ...but the audit log is write-only for this user: it can add rows, never read or change them.
DENY  SELECT ON trading.AuditLog TO mcp_reader;
GRANT INSERT ON trading.AuditLog TO mcp_reader;

-- No UPDATE / DELETE / ALTER anywhere. Writes to business tables fail at the database,
-- regardless of what the application code (or a prompt-injected LLM) attempts.

/* Dynamic Data Masking is already declared on Clients.Email and Accounts.AccountNumber.
   mcp_reader does NOT have UNMASK, so those columns come back masked, e.g.
       jXXX@XXXX.com      ACCXXXXX678
   To demo the difference:  GRANT UNMASK TO mcp_reader;   /   REVOKE UNMASK FROM mcp_reader;
   To hide sensitive data completely instead of masking it:
       DENY SELECT ON trading.Clients(Email) TO mcp_reader;                          */

-- Quick check, run in a session connected as mcp_reader:
--   SELECT TOP 5 * FROM trading.Clients;                    -- works, Email masked
--   UPDATE trading.Clients SET Country = 'X';               -- fails: permission denied
--   SELECT TOP 1 * FROM trading.AuditLog;                   -- fails: permission denied

GO

SET NOEXEC OFF;   -- re-enable execution if the safety check above skipped the script
GO
