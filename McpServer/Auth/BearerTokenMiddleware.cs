using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace TradingMcp.Auth;

/// <summary>
/// Minimal bearer-token authentication. Each client gets its own named token
/// (Mcp:Tokens:claude-desktop, Mcp:Tokens:inspector, ...), so the audit log shows WHICH client
/// made each call and one token can be revoked without touching the others.
///
/// This is the simple version, used while building the server. For production, validate JWTs from Microsoft Entra ID
/// with AddJwtBearer instead - you get expiry, rotation and per-user identity.
/// </summary>
public sealed class BearerTokenMiddleware
{
    private const int MinTokenLength = 32;

    private readonly RequestDelegate _next;
    private readonly (string Name, byte[] Hash)[] _tokens;

    public BearerTokenMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;

        var section = config.GetSection("Mcp:Tokens").GetChildren().ToList();

        if (section.Count == 0 || section.Any(c => string.IsNullOrEmpty(c.Value)))
            throw new InvalidOperationException(
                "No bearer tokens configured. Set Mcp:Tokens:<client-name>, e.g. " +
                "`dotnet user-secrets set \"Mcp:Tokens:inspector\" \"<random 32+ chars>\"`.");

        if (section.Any(c => c.Value!.Length < MinTokenLength))
            throw new InvalidOperationException($"Every token must be at least {MinTokenLength} characters.");

        // Only hashes are kept in memory; comparison is on equal-length digests.
        _tokens = section
            .Select(c => (Name: c.Key, Hash: SHA256.HashData(Encoding.UTF8.GetBytes(c.Value!))))
            .ToArray();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Anonymous health probe for the hosting platform. Exposes nothing.
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();

        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(header[7..].Trim()));

            foreach (var (name, hash) in _tokens)
            {
                if (CryptographicOperations.FixedTimeEquals(supplied, hash))
                {
                    context.User = new ClaimsPrincipal(
                        new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], authenticationType: "Bearer"));
                    await _next(context);
                    return;
                }
            }
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
    }
}
