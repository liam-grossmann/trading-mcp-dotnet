using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using TradingMcp.Auth;
using TradingMcp.Data;

namespace TradingMcp.Audit;

/// <summary>
/// Wraps every tool call: who called it, with what arguments, how long it took, how many rows came
/// back, and whether it succeeded. Database errors are logged in full on the server but only a generic
/// message goes back to the model, so schema details never leak into a conversation.
/// </summary>
public sealed class AuditService(
    TradingRepository repo,
    IHttpContextAccessor http,
    IConfiguration config,
    ILogger<AuditService> log)
{
    // Banks often require "no audit record, no data". Set Audit:FailClosed=true to refuse
    // to return results when the audit row can't be written.
    private readonly bool _failClosed = config.GetValue("Audit:FailClosed", false);

    public async Task<IReadOnlyList<T>> RunAsync<T>(
        string tool, object args, Func<Task<IReadOnlyList<T>>> action)
    {
        var caller = http.HttpContext?.User.GetCallerId() ?? "anonymous";
        var timer = Stopwatch.StartNew();
        IReadOnlyList<T>? rows = null;
        string? error = null;

        try
        {
            rows = await action();
            return rows;
        }
        catch (OperationCanceledException)
        {
            error = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            error = Truncate($"{ex.GetType().Name}: {ex.Message}", 500);
            log.LogError(ex, "Tool {Tool} failed for caller {Caller}", tool, caller);
            throw new McpException("The request could not be completed. Check the arguments and try again.");
        }
        finally
        {
            timer.Stop();
            await WriteAuditAsync(caller, tool, args, rows?.Count, (int)timer.ElapsedMilliseconds, error);
        }
    }

    private async Task WriteAuditAsync(
        string caller, string tool, object args, int? rowCount, int durationMs, string? error)
    {
        try
        {
            await repo.WriteAuditAsync(
                caller, tool, Truncate(JsonSerializer.Serialize(args), 4000),
                rowCount, durationMs, success: error is null, error);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AUDIT WRITE FAILED for tool {Tool}, caller {Caller}", tool, caller);
            if (_failClosed)
                throw new McpException("Audit log unavailable; request refused.");
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
