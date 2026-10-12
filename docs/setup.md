# Trading demo: read-only MCP server over SQL Server (C# / .NET)

A fully **synthetic** trading dataset and a **read-only MCP server** that exposes it to AI assistants.
No real companies, clients or market data: company names and tickers are invented, prices are simulated
(geometric Brownian motion), and client emails use the reserved `example.com` domain.

## Layout

```
sql/00_create_database.sql  creates the empty database (self-hosted SQL Server only; skip on Azure SQL)
sql/01_schema.sql        tables, constraints, indexes, masking, views, audit log
sql/02_security.sql      layer 1: read-only mcp_reader user (SELECT on the schema)
sql/03_procedures.sql    layer 2: stored procedures + EXECUTE-only permissions (supersedes the SELECT grant)
DataGenerator/           .NET 8 console app that fills the tables with synthetic data
McpServer/               ASP.NET Core minimal API hosting the MCP server (Dapper -> stored procedures)
entra-setup.md           Entra ID app registration, token testing and troubleshooting
```

## Data model

```
Instruments 1──* Prices          (daily OHLCV, PK = InstrumentId + PriceDate)
Instruments 1──* Trades
Clients     1──* Accounts 1──* Trades
AuditLog                          (one row per tool call, written by the server)

Views:  v_Positions           net holdings derived from trades
        v_PositionValuation   positions valued at the latest close (market value, unrealised P&L)
```

Simplifications to state on camera: no FX (accounts only trade in their base currency), no market holidays,
average-buy-price instead of tax lots/FIFO, no corporate actions.

## Security layers

| Layer | What it does |
|-------|--------------|
| Network/transport | HTTPS only when deployed (App Service provides it). |
| Authentication | Two modes: named static bearer tokens (local/dev) or Microsoft Entra ID JWTs (production). See below. |
| Rate limiting | 120 requests/minute per caller. |
| Database login | `mcp_reader` has **no table permissions**, only `EXECUTE` on the `mcp` schema. |
| Procedures | Fixed columns, hard row caps (25/50/200/250), no dynamic SQL. |
| Masking | `Email` and `AccountNumber` are masked (Dynamic Data Masking), so the agent works with `AccountId`. |
| Audit | Every call is logged (caller, tool, arguments, rows, duration, success). Optional fail-closed mode. |
| Tool annotations | Every tool is marked read-only so clients can show that to the user. |

## Quick start (local, Docker)

Requires Docker and the .NET 8+ SDK.

```bash
cp .env.example .env                  # then edit .env and set both passwords
docker compose up -d                  # starts SQL Server and waits until it is healthy
docker compose run --rm db-init       # creates the database and runs the three SQL scripts

cd DataGenerator
dotnet run -- "Server=localhost,1433;Database=TradingDemo;User ID=sa;Password=<SA_PASSWORD>;Encrypt=True;TrustServerCertificate=True" --reset
```

The MCP server then connects as the read-only user:

```bash
cd ../McpServer
dotnet user-secrets set "ConnectionStrings:Trading" "Server=localhost,1433;Database=TradingDemo;User ID=mcp_reader;Password=<MCP_READER_PASSWORD>;Encrypt=True;TrustServerCertificate=True"
```

Useful commands: `docker compose down` (stop, keep data), `docker compose down -v` (delete all data),
`RECREATE=1 docker compose run --rm db-init` (wipe and rebuild the database),
`STAGE=2 docker compose run --rm db-init` (stop before the stored procedures, then run `sql/03_procedures.sql` yourself).

Docker is for local development only. On Azure you create an Azure SQL Database in the portal and run the same
SQL scripts against it (see "Setup" below). `TrustServerCertificate=True` is for the local container's
self-signed certificate; do not use it against Azure SQL.

Apple Silicon: if the SQL Server container won't start, uncomment `platform: linux/amd64` in `docker-compose.yml`.

## Setup (any SQL Server, including Azure SQL)

1. **Create the database.**
   - Self-hosted SQL Server (Docker, Developer edition): run `sql/00_create_database.sql` as an admin, connected to `master`.
   - Azure SQL Database (free offer): create an empty database in the portal and skip script 00.
2. **Connect to the TradingDemo database, not `master`**, then run `sql/01_schema.sql` and `sql/02_security.sql` (replace the password placeholder) as an admin.
   Each script checks the current database and refuses to run in a system database.
3. **Generate data**, as the admin login (not `mcp_reader`):
   ```bash
   cd DataGenerator
   dotnet run -- "Server=...;Database=...;User ID=<admin>;Password=...;Encrypt=True" --reset
   ```
   Options: `--seed=42 --instruments=50 --clients=200 --years=3 --trades-per-account=40`.
4. Run `sql/03_procedures.sql` as an admin. This creates the procedures and removes direct table access from `mcp_reader`.
5. **Configure and run the server**:
   ```bash
   cd McpServer
   dotnet user-secrets set "ConnectionStrings:Trading" "Server=tcp:<server>.database.windows.net,1433;Database=<db>;User ID=mcp_reader;Password=...;Encrypt=True"
   dotnet user-secrets set "Mcp:Tokens:inspector" "$(openssl rand -hex 32)"
   dotnet run --urls http://localhost:5080
   ```
   The server starts only if at least one token (32+ characters) and a connection string are configured.
   Health check (no auth): `GET /health`. MCP endpoint: `POST /mcp`.

   http://localhost:5080/health  => OK.
   http://localhost:5080/mcp => 401 (coz we have no token)


*** Test

Next: connect with the token

Start Inspector in another terminal: npx @modelcontextprotocol/inspector

This opens a browser


Choose the Streamable HTTP transport.
Enter the URL http://localhost:5080/mcp.
Add your token from the user-secrets list (Mcp:Tokens:inspector). The UI differs between Inspector versions. If it has a “Bearer Token” field, paste just the token. If it has custom headers, add Authorization with the value Bearer <token>, including the word Bearer and the space.
Click Connect, then List Tools.







On Azure App Service the same settings are `ConnectionStrings__Trading` and `Mcp__Tokens__<name>`.

## Authentication modes

Set `Auth:Mode` (default `StaticTokens`):

| Mode | Use for | How it works |
|------|---------|--------------|
| `StaticTokens` | Local development, quick demos | Named tokens in `Mcp:Tokens:<name>`, compared by SHA-256 hash in constant time. |
| `Entra` | Production | Validates Entra ID JWTs (signature, issuer, audience, lifetime), requires the `mcp.read` scope or the `Mcp.Read` app role, and publishes OAuth protected-resource metadata so clients can discover the authorization server. |

Both modes feed the same caller identity into the audit log and the rate limiter.
Entra setup, token testing and troubleshooting are in [entra-setup.md](entra-setup.md).
**Read section 5 of that guide before promising that a particular interactive client works with Entra:**
Entra doesn't support Dynamic Client Registration.

## Try it

**MCP Inspector:**
```bash
npx @modelcontextprotocol/inspector
```
Choose the Streamable HTTP transport, URL `http://localhost:5080/mcp`, and add the header
`Authorization: Bearer <your token>`.

**Claude Code** (check the current docs if the flags have changed):
```bash
claude mcp add --transport http trading http://localhost:5080/mcp --header "Authorization: Bearer <your token>"
```

## Tools

| Tool | Purpose | Backing procedure |
|------|---------|-------------------|
| `get_dataset_info` | Date range and row counts (call first) | `mcp.GetDatasetInfo` |
| `search_instruments` | Find a ticker by name or prefix | `mcp.SearchInstruments` |
| `get_price_history` | Daily OHLCV for a ticker | `mcp.GetPriceHistory` |
| `get_top_movers` | Biggest daily movers | `mcp.GetTopMovers` |
| `find_accounts` | Find accounts by client name | `mcp.FindAccounts` |
| `list_trades` | An account's trades, paged | `mcp.ListTrades` |
| `get_positions` | Holdings with market value and P&L | `mcp.GetPositions` |
| `get_portfolio_summary` | Value and P&L by sector | `mcp.GetPortfolioSummary` |

## Demo prompts

- "What data do you have, and what's the latest date?"
- "Find accounts for a client named MacGyver and summarise one portfolio by sector."
- "Which holding in that account has lost the most since purchase?"
- "What were the five biggest movers on the last trading day?"
- "Show me every trade in <ticker> during March."
- Safety demos: "Delete all trades for this account." (no such tool; and the database would refuse anyway),
  and ask the assistant to run raw SQL (it can't).

Afterwards, show the audit trail as an admin: `SELECT TOP 20 * FROM trading.AuditLog ORDER BY AuditId DESC;`

## Notes and caveats

- Package versions are a baseline. Run `dotnet add package` to update, and check the MCP C# SDK docs,
  since the SDK and the auth story are still moving quickly.
- Remote MCP clients that insist on OAuth won't work with static bearer tokens. Clients that allow
  custom headers (Claude Code, MCP Inspector, VS Code) will.
- For episode 1 you can inline the SQL from `03_procedures.sql` into Dapper calls and keep the layer 1
  permissions, then move to procedures in episode 2.
