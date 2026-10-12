#!/bin/bash
# Creates the demo database and runs the SQL scripts in order. Runs inside the db-init container.
set -euo pipefail

DB="${DB_NAME:-TradingDemo}"
HOST="${SQL_HOST:-sqlserver}"

# Find sqlcmd (newer images ship it under mssql-tools18, older ones under mssql-tools).
SQLCMD=""
for candidate in /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd; do
  if [ -x "$candidate" ]; then SQLCMD="$candidate"; break; fi
done
if [ -z "$SQLCMD" ]; then echo "sqlcmd not found in this image." >&2; exit 1; fi

# -C trusts the container's self-signed certificate; -b makes sqlcmd fail on the first SQL error.
run() { "$SQLCMD" -S "$HOST" -U sa -P "$SA_PASSWORD" -C -b "$@"; }

exists=$(run -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name = N'$DB';" | tr -d '[:space:]')

if [ "$exists" = "1" ]; then
  if [ "${RECREATE:-0}" != "1" ]; then
    echo "Database '$DB' already exists. Nothing to do."
    echo "To wipe it and start again:  RECREATE=1 docker compose run --rm db-init"
    exit 0
  fi
  echo "RECREATE=1: dropping database '$DB'..."
  run -Q "ALTER DATABASE [$DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$DB];"
fi

echo "Creating database '$DB'..."
# sql/00_create_database.sql does the work; sed swaps in a custom name if DB_NAME was changed.
sed "s/TradingDemo/${DB}/g" /sql/00_create_database.sql > /tmp/00_create_database.sql
run -i /tmp/00_create_database.sql
rm -f /tmp/00_create_database.sql

echo "Running 01_schema.sql..."
run -d "$DB" -i /sql/01_schema.sql

echo "Running 02_security.sql..."
# Substitute the mcp_reader password into a temporary copy; the file on disk is untouched.
sed "s|<<replace-with-a-long-random-password>>|${MCP_READER_PASSWORD}|" /sql/02_security.sql > /tmp/02_security.sql
run -d "$DB" -i /tmp/02_security.sql
rm -f /tmp/02_security.sql

# STAGE=2 stops after the security layer-1 script, so you can run 03_procedures.sql by hand (used in video 2).
if [ "${STAGE:-3}" -ge 3 ]; then
  echo "Running 03_procedures.sql..."
  run -d "$DB" -i /sql/03_procedures.sql
else
  echo "STAGE=${STAGE}: skipping 03_procedures.sql. Run it yourself when you are ready."
fi

cat <<EOF

Done. Database '$DB' is ready on localhost,1433 with the schema, the read-only mcp_reader user and the stored procedures.

Next, load the synthetic data (as sa):
  cd DataGenerator
  dotnet run -- "Server=localhost,1433;Database=$DB;User ID=sa;Password=<SA_PASSWORD>;Encrypt=True;TrustServerCertificate=True" --reset

Connection string for the MCP server (as mcp_reader):
  Server=localhost,1433;Database=$DB;User ID=mcp_reader;Password=<MCP_READER_PASSWORD>;Encrypt=True;TrustServerCertificate=True
EOF
