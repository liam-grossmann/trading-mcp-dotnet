using System.ComponentModel;
using ModelContextProtocol.Server;
using TradingMcp.Audit;
using TradingMcp.Data;

namespace TradingMcp.Tools;

/// <summary>
/// Read-only tools over the synthetic trading database. All data is fictitious.
/// Each tool is a one-liner: audit wrapper -> repository -> stored procedure.
/// The descriptions matter: they are what the model reads to decide which tool to call.
/// </summary>
[McpServerToolType]
public sealed class TradingTools(TradingRepository repo, AuditService audit)
{
    [McpServerTool(Name = "get_dataset_info", ReadOnly = true)]
    [Description("Describes the dataset: first and last price date, and how many instruments, accounts and trades exist. " +
                 "Call this first to learn which dates are available before asking about 'last month' or 'latest'.")]
    public Task<IReadOnlyList<DatasetInfoDto>> GetDatasetInfo(CancellationToken ct = default) =>
        audit.RunAsync("get_dataset_info", new { },
            () => repo.GetDatasetInfo(ct));

    [McpServerTool(Name = "search_instruments", ReadOnly = true)]
    [Description("Finds fictitious listed companies by ticker prefix or part of the company name. Returns at most 25.")]
    public Task<IReadOnlyList<InstrumentDto>> SearchInstruments(
        [Description("Ticker prefix or part of a company name, e.g. 'MAR' or 'Pharma'.")] string text,
        CancellationToken ct = default) =>
        audit.RunAsync("search_instruments", new { text },
            () => repo.SearchInstruments(text, ct));

    [McpServerTool(Name = "get_price_history", ReadOnly = true)]
    [Description("Daily open/high/low/close/volume for one ticker, oldest first. " +
                 "Returns at most the 250 most recent trading days inside the requested range.")]
    public Task<IReadOnlyList<PriceDto>> GetPriceHistory(
        [Description("Exact ticker, e.g. 'MARP'. Use search_instruments if unsure.")] string ticker,
        [Description("Start date, yyyy-MM-dd. Optional.")] DateOnly? from = null,
        [Description("End date, yyyy-MM-dd. Optional.")] DateOnly? to = null,
        CancellationToken ct = default) =>
        audit.RunAsync("get_price_history", new { ticker, from, to },
            () => repo.GetPriceHistory(ticker, from, to, ct));

    [McpServerTool(Name = "get_top_movers", ReadOnly = true)]
    [Description("The instruments with the largest absolute daily % price change on a given trading day. " +
                 "A weekend or holiday date snaps back to the previous trading day.")]
    public Task<IReadOnlyList<MoverDto>> GetTopMovers(
        [Description("Trading day, yyyy-MM-dd. Defaults to the latest day available.")] DateOnly? date = null,
        [Description("How many to return, 1-20. Default 5.")] int count = 5,
        CancellationToken ct = default) =>
        audit.RunAsync("get_top_movers", new { date, count },
            () => repo.GetTopMovers(date, count, ct));

    [McpServerTool(Name = "find_accounts", ReadOnly = true)]
    [Description("Finds client accounts by (part of) the client's name. Returns at most 25. " +
                 "Account numbers are masked for privacy, so use the returned accountId in the other tools.")]
    public Task<IReadOnlyList<AccountDto>> FindAccounts(
        [Description("At least 2 characters of the client's name, e.g. 'Smith'.")] string name,
        CancellationToken ct = default) =>
        audit.RunAsync("find_accounts", new { name },
            () => repo.FindAccounts(name, ct));

    [McpServerTool(Name = "list_trades", ReadOnly = true)]
    [Description("An account's trades, newest first, 50 per page. Optionally filter by date range or ticker.")]
    public Task<IReadOnlyList<TradeDto>> ListTrades(
        [Description("The accountId returned by find_accounts.")] int accountId,
        [Description("Start date, yyyy-MM-dd. Optional.")] DateOnly? from = null,
        [Description("End date, yyyy-MM-dd (inclusive). Optional.")] DateOnly? to = null,
        [Description("Only trades in this exact ticker. Optional.")] string? ticker = null,
        [Description("Page number, starting at 1. Default 1.")] int page = 1,
        CancellationToken ct = default) =>
        audit.RunAsync("list_trades", new { accountId, from, to, ticker, page },
            () => repo.ListTrades(accountId, from, to, ticker, page, ct));

    [McpServerTool(Name = "get_positions", ReadOnly = true)]
    [Description("Current holdings for an account, valued at the latest close: quantity, average buy price, " +
                 "market value and unrealised profit/loss (amount and %), largest first.")]
    public Task<IReadOnlyList<PositionDto>> GetPositions(
        [Description("The accountId returned by find_accounts.")] int accountId,
        CancellationToken ct = default) =>
        audit.RunAsync("get_positions", new { accountId },
            () => repo.GetPositions(accountId, ct));

    [McpServerTool(Name = "get_portfolio_summary", ReadOnly = true)]
    [Description("An account's market value and unrealised P&L grouped by sector, with a TOTAL row.")]
    public Task<IReadOnlyList<SectorSummaryDto>> GetPortfolioSummary(
        [Description("The accountId returned by find_accounts.")] int accountId,
        CancellationToken ct = default) =>
        audit.RunAsync("get_portfolio_summary", new { accountId },
            () => repo.GetPortfolioSummary(accountId, ct));
}
