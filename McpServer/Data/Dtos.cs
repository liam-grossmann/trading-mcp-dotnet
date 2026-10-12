namespace TradingMcp.Data;

// Dapper maps result columns to these constructor parameters by name and type,
// so the stored procedures alias their columns to match (e.g. OpenPrice AS [Open]).

public record DatasetInfoDto(
    DateTime FirstPriceDate, DateTime LastPriceDate, int Instruments, int Accounts, int Trades);

public record InstrumentDto(string Ticker, string Name, string Sector, string Currency);

public record PriceDto(
    DateTime PriceDate, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

public record MoverDto(
    string Ticker, string Name, DateTime PriceDate, decimal ClosePrice, decimal PrevClose, decimal ChangePct);

public record AccountDto(
    int AccountId, string AccountNumber, string ClientName, string Country, string RiskProfile,
    string AccountType, string BaseCurrency, DateTime OpenedOn);

public record TradeDto(
    long TradeId, DateTime TradeTimestamp, string Ticker, string Side,
    decimal Quantity, decimal Price, decimal Fees, string Status);

public record PositionDto(
    string Ticker, string InstrumentName, string Sector, string Currency,
    decimal NetQuantity, decimal? AvgBuyPrice, DateTime PriceAsOf, decimal LastPrice,
    decimal MarketValue, decimal UnrealisedPnL, decimal? UnrealisedPnLPct);

public record SectorSummaryDto(
    string Sector, string Currency, int Positions, decimal MarketValue, decimal UnrealisedPnL);
