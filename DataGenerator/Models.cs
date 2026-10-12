namespace TradingDataGenerator;

public record Instrument(
    int Id, string Ticker, string Name, string Sector, string Currency,
    double StartPrice, double Mu, double Sigma);   // Mu = annual drift, Sigma = annual volatility

public record PriceBar(
    int InstrumentId, DateTime PriceDate,
    decimal Open, decimal High, decimal Low, decimal Close, long Volume);

public record Client(
    int Id, string FullName, string Email, string Country, string RiskProfile, DateTime CreatedOn);

public record Account(
    int Id, int ClientId, string AccountNumber, string AccountType, string BaseCurrency, DateTime OpenedOn);

public record Trade(
    int AccountId, int InstrumentId, string Side, decimal Quantity, decimal Price,
    decimal Fees, DateTime Timestamp, string Status);
