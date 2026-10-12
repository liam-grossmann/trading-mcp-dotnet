using System.Text.RegularExpressions;
using Bogus;

namespace TradingDataGenerator;

/// <summary>
/// Builds a fully synthetic trading dataset. Everything is driven by a seeded Random,
/// so the same --seed always produces the same data (handy for repeatable demos).
/// </summary>
public static class Generators
{
    // Invented company-name parts. Combined as "<Prefix> <Suffix>", e.g. "Marlowe Pharma".
    private static readonly string[] Prefixes =
    {
        "Aldergate", "Brightwater", "Cinderfall", "Dunmore", "Eastvale", "Falconer", "Glenhaven",
        "Harrowgate", "Ironbridge", "Juniper", "Kestrel", "Lakemont", "Marlowe", "Northgate",
        "Oakhurst", "Pinecrest", "Quillfield", "Redstone", "Silverpine", "Thornbury", "Umberfield",
        "Valewood", "Westmere", "Yarrow", "Zephyrine"
    };

    // Suffix -> (sector, annual drift, annual volatility). Gives each sector a believable "personality".
    private static readonly Dictionary<string, (string Sector, double Mu, double Sigma)> Suffixes = new()
    {
        ["Holdings"]  = ("Financials",             0.07, 0.22),
        ["Bank"]      = ("Financials",             0.06, 0.20),
        ["Capital"]   = ("Financials",             0.08, 0.25),
        ["Insurance"] = ("Financials",             0.05, 0.16),
        ["Energy"]    = ("Energy",                 0.05, 0.32),
        ["Utilities"] = ("Utilities",              0.04, 0.14),
        ["Pharma"]    = ("Healthcare",             0.09, 0.28),
        ["Systems"]   = ("Technology",             0.12, 0.35),
        ["Networks"]  = ("Technology",             0.11, 0.33),
        ["Foods"]     = ("Consumer Staples",       0.05, 0.15),
        ["Retail"]    = ("Consumer Discretionary", 0.06, 0.24),
        ["Motors"]    = ("Consumer Discretionary", 0.06, 0.30),
        ["Logistics"] = ("Industrials",            0.07, 0.21),
        ["Materials"] = ("Materials",              0.06, 0.26),
        ["Media"]     = ("Communication",          0.07, 0.27),
    };

    // ---------------------------------------------------------------- calendar

    /// <summary>Weekdays only. (Public holidays are ignored - fine for a demo.)</summary>
    public static List<DateTime> TradingDays(DateTime start, DateTime end)
    {
        var days = new List<DateTime>();
        for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                days.Add(d);
        }
        return days;
    }

    // ------------------------------------------------------------- instruments

    public static List<Instrument> Instruments(Random rnd, int count)
    {
        count = Math.Min(count, Prefixes.Length * Suffixes.Count);   // can't make more unique names than this

        var suffixKeys = Suffixes.Keys.ToArray();
        var currencies = new[] { "USD", "USD", "USD", "USD", "USD", "USD", "GBP", "GBP", "EUR", "EUR" };
        var names = new HashSet<string>();
        var tickers = new HashSet<string>();
        var result = new List<Instrument>(count);

        while (result.Count < count)
        {
            var prefix = Prefixes[rnd.Next(Prefixes.Length)];
            var suffix = suffixKeys[rnd.Next(suffixKeys.Length)];
            var name = $"{prefix} {suffix}";
            if (!names.Add(name)) continue;

            var (sector, mu, sigma) = Suffixes[suffix];

            result.Add(new Instrument(
                Id: result.Count + 1,
                Ticker: MakeTicker(rnd, prefix, suffix, tickers),
                Name: name,
                Sector: sector,
                Currency: currencies[rnd.Next(currencies.Length)],
                StartPrice: Math.Clamp(Math.Round(10 * Math.Exp(1.5 + 0.8 * Gaussian(rnd)), 2), 5, 600),
                Mu: mu + (rnd.NextDouble() - 0.5) * 0.04,
                Sigma: sigma * (0.85 + rnd.NextDouble() * 0.3)));
        }
        return result;
    }

    private static string MakeTicker(Random rnd, string prefix, string suffix, HashSet<string> used)
    {
        var candidates = new[]
        {
            (prefix[..3] + suffix[..1]).ToUpperInvariant(),   // MARP
            (prefix[..2] + suffix[..2]).ToUpperInvariant(),   // MAPH
        };
        foreach (var c in candidates)
            if (used.Add(c)) return c;

        string t;
        do { t = (prefix[..3] + (char)('A' + rnd.Next(26))).ToUpperInvariant(); }
        while (!used.Add(t));
        return t;
    }

    // ------------------------------------------------------------------ prices

    /// <summary>
    /// Daily OHLCV bars using geometric Brownian motion:
    ///   S(t+1) = S(t) * exp((mu - sigma^2/2)*dt + sigma*sqrt(dt)*Z),  Z ~ N(0,1),  dt = 1/252.
    /// </summary>
    public static List<PriceBar> Prices(Random rnd, List<Instrument> instruments, List<DateTime> days)
    {
        const double dt = 1.0 / 252.0;
        var bars = new List<PriceBar>(instruments.Count * days.Count);

        foreach (var inst in instruments)
        {
            var prevClose = inst.StartPrice;
            var baseVolume = 200_000 + rnd.Next(0, 3_000_000);

            foreach (var day in days)
            {
                var z = Gaussian(rnd);
                var close = prevClose * Math.Exp((inst.Mu - 0.5 * inst.Sigma * inst.Sigma) * dt
                                                 + inst.Sigma * Math.Sqrt(dt) * z);
                close = Math.Max(close, 1.0);

                var open = prevClose * (1 + (rnd.NextDouble() - 0.5) * 0.01);       // small overnight gap
                var high = Math.Max(open, close) * (1 + rnd.NextDouble() * 0.01);
                var low  = Math.Min(open, close) * (1 - rnd.NextDouble() * 0.01);
                var volume = (long)(baseVolume * Math.Exp(0.4 * Gaussian(rnd)));

                bars.Add(new PriceBar(
                    inst.Id, day,
                    Money(open), Money(high), Money(Math.Max(low, 0.5)), Money(close),
                    volume));

                prevClose = close;
            }
        }
        return bars;
    }

    // ------------------------------------------------------- clients & accounts

    public static List<Client> Clients(int seed, int count, DateTime firstDay)
    {
        var faker = new Faker("en_GB") { Random = new Randomizer(seed) };
        var countries = new[] { "United Kingdom", "United States", "Germany", "France", "Ireland", "Netherlands", "Spain", "Italy" };
        var profiles = new[] { "Conservative", "Balanced", "Growth", "Aggressive" };

        var list = new List<Client>(count);
        for (var i = 1; i <= count; i++)
        {
            var first = faker.Name.FirstName();
            var last = faker.Name.LastName();

            // example.com is reserved for documentation, so no real person can ever receive mail from this data.
            var local = Regex.Replace($"{first}.{last}".ToLowerInvariant(), "[^a-z.]", "");

            list.Add(new Client(
                Id: i,
                FullName: $"{first} {last}",
                Email: $"{local}@example.com",
                Country: faker.Random.ArrayElement(countries),
                RiskProfile: faker.Random.ArrayElement(profiles),
                CreatedOn: firstDay.AddDays(-faker.Random.Int(30, 1500))));
        }
        return list;
    }

    public static List<Account> Accounts(
        Random rnd, List<Client> clients, DateTime firstDay, DateTime lastDay, IReadOnlyList<string> currencies)
    {
        var types = new[] { "Brokerage", "Brokerage", "Retirement", "Joint" };
        var numbers = new HashSet<string>();
        var maxOffset = Math.Max(1, (int)(lastDay - firstDay).TotalDays - 120);   // leave >= ~4 months of trading history
        var list = new List<Account>();

        foreach (var client in clients)
        {
            var accountCount = rnd.NextDouble() switch { < 0.6 => 1, < 0.9 => 2, _ => 3 };
            for (var n = 0; n < accountCount; n++)
            {
                string number;
                do { number = $"ACC{rnd.Next(10_000_000, 99_999_999)}"; } while (!numbers.Add(number));

                list.Add(new Account(
                    Id: list.Count + 1,
                    ClientId: client.Id,
                    AccountNumber: number,
                    AccountType: types[rnd.Next(types.Length)],
                    BaseCurrency: currencies[rnd.Next(currencies.Count)],
                    OpenedOn: firstDay.AddDays(rnd.Next(0, maxOffset))));
            }
        }
        return list;
    }

    // ------------------------------------------------------------------ trades

    /// <summary>
    /// Simulates each account's trading chronologically so it can never sell shares it doesn't hold
    /// (no short positions). Accounts only trade instruments quoted in their base currency, which
    /// avoids needing FX rates in the demo.
    /// </summary>
    public static List<Trade> Trades(
        Random rnd, List<Account> accounts, List<Instrument> instruments,
        Dictionary<(int, DateTime), decimal> closes, List<DateTime> tradingDays, int avgTradesPerAccount)
    {
        var byCurrency = instruments.GroupBy(i => i.Currency).ToDictionary(g => g.Key, g => g.ToList());
        var lastDay = tradingDays[^1];
        var trades = new List<Trade>();

        foreach (var acct in accounts)
        {
            var pool = byCurrency[acct.BaseCurrency];
            var eligible = tradingDays.Where(d => d >= acct.OpenedOn).ToList();
            if (eligible.Count == 0) continue;

            var n = rnd.Next(Math.Max(3, avgTradesPerAccount / 2), avgTradesPerAccount * 2);
            var chosenDays = Enumerable.Range(0, n)
                                       .Select(_ => eligible[rnd.Next(eligible.Count)])
                                       .OrderBy(d => d)
                                       .ToList();

            var holdings = new Dictionary<int, decimal>();   // instrumentId -> shares held

            foreach (var day in chosenDays)
            {
                var isSell = holdings.Count > 0 && rnd.NextDouble() < 0.35;
                int instrumentId;
                decimal qty;

                if (isSell)
                {
                    var held = holdings.Keys.ToList();
                    instrumentId = held[rnd.Next(held.Count)];
                    var owned = holdings[instrumentId];

                    qty = rnd.NextDouble() < 0.5
                        ? owned                                                                   // close the whole position
                        : Math.Max(1m, Math.Floor(owned * (decimal)(0.2 + rnd.NextDouble() * 0.6))); // partial sell

                    holdings[instrumentId] = owned - qty;
                    if (holdings[instrumentId] == 0) holdings.Remove(instrumentId);
                }
                else
                {
                    instrumentId = pool[rnd.Next(pool.Count)].Id;
                    qty = rnd.Next(1, 51) * 10;                                                   // 10..500 shares
                    holdings[instrumentId] = holdings.GetValueOrDefault(instrumentId) + qty;
                }

                var close = closes[(instrumentId, day)];
                var price = Math.Round(close * (decimal)(1 + (rnd.NextDouble() - 0.5) * 0.004), 2);   // +/-0.2% around the close
                var fees = Math.Round(Math.Max(1.00m, qty * price * 0.0005m), 2);                    // 5 bps, min 1.00
                var ts = day.AddHours(9.5).AddMinutes(rnd.Next(0, 390)).AddSeconds(rnd.Next(0, 60)); // 09:30-16:00

                trades.Add(new Trade(
                    acct.Id, instrumentId, isSell ? "SELL" : "BUY", qty, price, fees, ts,
                    Status: day >= lastDay.AddDays(-2) ? "PENDING" : "SETTLED"));
            }
        }

        // Insert in time order so TradeId increases with time.
        return trades.OrderBy(t => t.Timestamp).ToList();
    }

    // ----------------------------------------------------------------- helpers

    private static decimal Money(double value) => (decimal)Math.Round(value, 2);

    /// <summary>Standard normal sample via the Box-Muller transform.</summary>
    private static double Gaussian(Random rnd)
    {
        var u1 = 1.0 - rnd.NextDouble();
        var u2 = rnd.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
