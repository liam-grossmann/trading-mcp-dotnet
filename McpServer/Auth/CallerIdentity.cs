using System.Security.Claims;

namespace TradingMcp.Auth;

public static class CallerIdentity
{
    /// <summary>
    /// A stable, human-readable identifier for whoever is calling, used in the audit log and
    /// as the rate-limit key. Works for both authentication modes:
    ///   static tokens   -> the token's name, e.g. "inspector"
    ///   Entra user      -> "alice@contoso.com [oid:...]"   (UPN is readable, oid never changes)
    ///   Entra app-only  -> "app:&lt;client id&gt; [oid:...]"  (client-credentials tokens have no user)
    /// </summary>
    public static string GetCallerId(this ClaimsPrincipal? user)
    {
        if (user is null || user.Identity?.IsAuthenticated != true)
            return "anonymous";

        var name = user.FindFirst("preferred_username")?.Value ?? user.Identity?.Name;
        var oid = user.FindFirst("oid")?.Value;

        if (name is null)
        {
            var app = user.FindFirst("azp")?.Value ?? user.FindFirst("appid")?.Value;
            name = app is not null ? $"app:{app}" : user.FindFirst("sub")?.Value ?? "unknown";
        }

        return oid is null ? name : $"{name} [oid:{oid}]";
    }
}
