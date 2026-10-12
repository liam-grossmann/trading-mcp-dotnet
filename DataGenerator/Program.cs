using System.Data;
using Microsoft.Data.SqlClient;
using TradingDataGenerator;

// Usage:
//   dotnet run -- "<connection string>" [--reset] [--seed=42] [--instruments=50] [--clients=200]
//                                       [--years=3] [--trades-per-account=40]
// The connection string can also come from the TRADING_DB_CONNECTION environment variable.
// Run with an ADMIN login (not mcp_reader - that user is read-only by design).

var opts = args.Where(a => a.StartsWith("--"))
               .Select(a => a[2..].Split('=', 2))
               .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "true");

int Opt(string key, int fallback) =>
    opts.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

var connStr = args.FirstOrDefault(a => !a.StartsWith("--"))
              ?? Environment.GetEnvironmentVariable("TRADING_DB_CONNECTION");

if (string.IsNullOrWhiteSpace(connStr))
{
    Console.Error.WriteLine("No connection string. Pass it as the first argument or set TRADING_DB_CONNECTION.");
    return 1;
}

// ---------------------------------------------------------------- generate
var seed = Opt("seed", 42);
var rnd = new Random(seed);

var end = DateTime.UtcNow.Date.AddDays(-1);
var start = end.AddYears(-Opt("years", 3));

Console.WriteLine($"Generating synthetic data (seed {seed}, {start:yyyy-MM-dd} to {end:yyyy-MM-dd})...");

var days = Generators.TradingDays(start, end);
var instruments = Generators.Instruments(rnd, Opt("instruments", 50));
var bars = Generators.Prices(rnd, instruments, days);
var closes = bars.ToDictionary(b => (b.InstrumentId, b.PriceDate), b => b.Close);
var clients = Generators.Clients(seed, Opt("clients", 200), days[0]);
var currencies = instruments.Select(i => i.Currency).Distinct().ToList();
var accounts = Generators.Accounts(rnd, clients, days[0], days[^1], currencies);
var trades = Generators.Trades(rnd, accounts, instruments, closes, days, Opt("trades-per-account", 40));

// ------------------------------------------------------------------ insert
await using var conn = new SqlConnection(connStr);
await conn.OpenAsync();
await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();   // all-or-nothing

async Task<int> Scalar(string sql)
{
    await using var cmd = new SqlCommand(sql, conn, tx);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync());
}

async Task Exec(string sql)
{
    await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 120 };
    await cmd.ExecuteNonQueryAsync();
}

async Task Bulk(string table, DataTable data, bool keepIdentity)
{
    var options = SqlBulkCopyOptions.TableLock | (keepIdentity ? SqlBulkCopyOptions.KeepIdentity : SqlBulkCopyOptions.Default);
    using var bulk = new SqlBulkCopy(conn, options, tx)
    {
        DestinationTableName = table,
        BatchSize = 5000,
        BulkCopyTimeout = 300
    };
    foreach (DataColumn c in data.Columns)
        bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);

    await bulk.WriteToServerAsync(data);
    Console.WriteLine($"  {table,-22} {data.Rows.Count,9:N0} rows");
}

DataTable NewTable(params (string Name, Type Type)[] columns)
{
    var t = new DataTable();
    foreach (var (name, type) in columns) t.Columns.Add(name, type);
    return t;
}

if (await Scalar("SELECT COUNT(*) FROM trading.Instruments") > 0)
{
    if (!opts.ContainsKey("reset"))
    {
        Console.Error.WriteLine("The database already contains data. Re-run with --reset to wipe and regenerate it.");
        return 2;
    }

    Console.WriteLine("Clearing existing data (AuditLog is left alone)...");
    await Exec("DELETE FROM trading.Trades");
    await Exec("DELETE FROM trading.Prices");
    await Exec("DELETE FROM trading.Accounts");
    await Exec("DELETE FROM trading.Clients");
    await Exec("DELETE FROM trading.Instruments");
    await Exec("DBCC CHECKIDENT ('trading.Trades', RESEED, 0)");
}

Console.WriteLine("Inserting...");

var tInstruments = NewTable(("InstrumentId", typeof(int)), ("Ticker", typeof(string)), ("Name", typeof(string)),
                            ("Sector", typeof(string)), ("Currency", typeof(string)));
foreach (var i in instruments) tInstruments.Rows.Add(i.Id, i.Ticker, i.Name, i.Sector, i.Currency);
await Bulk("trading.Instruments", tInstruments, keepIdentity: true);

var tPrices = NewTable(("InstrumentId", typeof(int)), ("PriceDate", typeof(DateTime)),
                       ("OpenPrice", typeof(decimal)), ("HighPrice", typeof(decimal)),
                       ("LowPrice", typeof(decimal)), ("ClosePrice", typeof(decimal)), ("Volume", typeof(long)));
foreach (var b in bars) tPrices.Rows.Add(b.InstrumentId, b.PriceDate, b.Open, b.High, b.Low, b.Close, b.Volume);
await Bulk("trading.Prices", tPrices, keepIdentity: false);

var tClients = NewTable(("ClientId", typeof(int)), ("FullName", typeof(string)), ("Email", typeof(string)),
                        ("Country", typeof(string)), ("RiskProfile", typeof(string)), ("CreatedOn", typeof(DateTime)));
foreach (var c in clients) tClients.Rows.Add(c.Id, c.FullName, c.Email, c.Country, c.RiskProfile, c.CreatedOn);
await Bulk("trading.Clients", tClients, keepIdentity: true);

var tAccounts = NewTable(("AccountId", typeof(int)), ("ClientId", typeof(int)), ("AccountNumber", typeof(string)),
                         ("AccountType", typeof(string)), ("BaseCurrency", typeof(string)), ("OpenedOn", typeof(DateTime)));
foreach (var a in accounts) tAccounts.Rows.Add(a.Id, a.ClientId, a.AccountNumber, a.AccountType, a.BaseCurrency, a.OpenedOn);
await Bulk("trading.Accounts", tAccounts, keepIdentity: true);

// TradeId is left out so SQL Server assigns it (identity), in the time order we sorted by.
var tTrades = NewTable(("AccountId", typeof(int)), ("InstrumentId", typeof(int)), ("Side", typeof(string)),
                       ("Quantity", typeof(decimal)), ("Price", typeof(decimal)), ("Fees", typeof(decimal)),
                       ("TradeTimestamp", typeof(DateTime)), ("Status", typeof(string)));
foreach (var t in trades) tTrades.Rows.Add(t.AccountId, t.InstrumentId, t.Side, t.Quantity, t.Price, t.Fees, t.Timestamp, t.Status);
await Bulk("trading.Trades", tTrades, keepIdentity: false);

await tx.CommitAsync();

var openPositions = await Scalar("SELECT COUNT(*) FROM trading.v_Positions");
Console.WriteLine($"Done. {openPositions:N0} open positions across {accounts.Count:N0} accounts.");
return 0;
