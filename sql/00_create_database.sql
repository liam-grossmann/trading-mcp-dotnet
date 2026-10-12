/* =====================================================================
   Step 0: create the (empty) TradingDemo database.
   For SQL Server you run yourself (Docker, Developer edition, a VM).
   Run connected to the master database as an admin (e.g. sa).

   AZURE SQL DATABASE: skip this script. Create an empty database in the portal instead.
   Azure SQL is always a "contained" database, so the setup below is built in.

   After this, connect to TradingDemo (NOT master) before running 01, 02 and 03.
   ===================================================================== */

USE master;
GO

-- Contained database authentication lets 02_security.sql use CREATE USER ... WITH PASSWORD,
-- exactly as on Azure SQL. That keeps the SQL scripts identical for local and cloud.
EXEC sp_configure 'contained database authentication', 1;
RECONFIGURE;
GO

IF DB_ID(N'TradingDemo') IS NULL
    CREATE DATABASE TradingDemo;
GO

ALTER DATABASE TradingDemo SET CONTAINMENT = PARTIAL;
GO
