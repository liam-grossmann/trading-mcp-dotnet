using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace TradingMcp.Data;

/// <summary>
/// The server's entire view of the database. Every method is a single stored-procedure call
/// (schema "mcp"), and the SQL login behind it has EXECUTE on that schema and nothing else.
/// </summary>
public sealed class TradingRepository(Func<SqlConnection> connect)
{
    private const int QueryTimeoutSeconds = 15;

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string procedure, object? args, CancellationToken ct)
    {
        await using var conn = connect();
        var rows = await conn.QueryAsync<T>(new CommandDefinition(
            procedure, args,
            commandType: CommandType.StoredProcedure,
            commandTimeout: QueryTimeoutSeconds,
            cancellationToken: ct));
        return rows.AsList();
    }

    // Dapper parameters are plain DateTime, so DateOnly is converted here.
    private static DateTime? ToDate(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);

    public Task<IReadOnlyList<DatasetInfoDto>> GetDatasetInfo(CancellationToken ct) =>
        QueryAsync<DatasetInfoDto>("mcp.GetDatasetInfo", null, ct);

    public Task<IReadOnlyList<InstrumentDto>> SearchInstruments(string text, CancellationToken ct) =>
        QueryAsync<InstrumentDto>("mcp.SearchInstruments", new { Text = text }, ct);

    public Task<IReadOnlyList<PriceDto>> GetPriceHistory(string ticker, DateOnly? from, DateOnly? to, CancellationToken ct) =>
        QueryAsync<PriceDto>("mcp.GetPriceHistory", new { Ticker = ticker, From = ToDate(from), To = ToDate(to) }, ct);

    public Task<IReadOnlyList<MoverDto>> GetTopMovers(DateOnly? date, int count, CancellationToken ct) =>
        QueryAsync<MoverDto>("mcp.GetTopMovers", new { Date = ToDate(date), Count = count }, ct);

    public Task<IReadOnlyList<AccountDto>> FindAccounts(string name, CancellationToken ct) =>
        QueryAsync<AccountDto>("mcp.FindAccounts", new { Name = name }, ct);

    public Task<IReadOnlyList<TradeDto>> ListTrades(
        int accountId, DateOnly? from, DateOnly? to, string? ticker, int page, CancellationToken ct) =>
        QueryAsync<TradeDto>("mcp.ListTrades",
            new { AccountId = accountId, From = ToDate(from), To = ToDate(to), Ticker = ticker, Page = page }, ct);

    public Task<IReadOnlyList<PositionDto>> GetPositions(int accountId, CancellationToken ct) =>
        QueryAsync<PositionDto>("mcp.GetPositions", new { AccountId = accountId }, ct);

    public Task<IReadOnlyList<SectorSummaryDto>> GetPortfolioSummary(int accountId, CancellationToken ct) =>
        QueryAsync<SectorSummaryDto>("mcp.GetPortfolioSummary", new { AccountId = accountId }, ct);

    /// <summary>Writes one audit row. Deliberately not cancellable: an audit entry should survive a cancelled request.</summary>
    public async Task WriteAuditAsync(
        string caller, string tool, string parameters, int? rows, int durationMs, bool success, string? error)
    {
        await using var conn = connect();
        await conn.ExecuteAsync(new CommandDefinition(
            "mcp.WriteAudit",
            new
            {
                Caller = caller,
                ToolName = tool,
                Parameters = parameters,
                RowsReturned = rows,
                DurationMs = durationMs,
                Success = success,
                Error = error
            },
            commandType: CommandType.StoredProcedure,
            commandTimeout: 5));
    }
}
