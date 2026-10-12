using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using TradingMcp.Audit;
using TradingMcp.Auth;
using TradingMcp.Data;

var builder = WebApplication.CreateBuilder(args);

// Connection string for the read-only mcp_reader user. Keep it in user-secrets locally
// and in App Service settings (ConnectionStrings__Trading) when deployed - never in source control.
var connectionString = builder.Configuration.GetConnectionString("Trading");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:Trading is not configured.");

// Auth:Mode = "StaticTokens" (local dev, quick demos) or "Entra" (Microsoft Entra ID JWTs).
var authMode = builder.Configuration["Auth:Mode"] ?? "StaticTokens";
var useEntra = authMode.Equals("Entra", StringComparison.OrdinalIgnoreCase);
if (!useEntra && !authMode.Equals("StaticTokens", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Unknown Auth:Mode '{authMode}'. Use 'StaticTokens' or 'Entra'.");

builder.Services.AddSingleton<Func<SqlConnection>>(_ => () => new SqlConnection(connectionString));
builder.Services.AddSingleton<TradingRepository>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<AuditService>();

if (useEntra)
    builder.Services.AddEntraAuth(builder.Configuration);

// Rate Limiter. Throttle per caller (token name, or Entra user/app identity): 120 requests / minute.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.GetCallerId(),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1)
            }));
});

builder.Services
    .AddMcpServer()
    .WithHttpTransport(http => http.Stateless = true)   // read-only and sessionless: easy to scale, fine on a free tier
    .WithToolsFromAssembly();

var app = builder.Build();
app.Logger.LogInformation("Authentication mode: {Mode}", useEntra ? "Entra" : "StaticTokens");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));   // anonymous: no RequireAuthorization, skipped by the token middleware

if (useEntra)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
else
{
    app.UseMiddleware<BearerTokenMiddleware>();   // must run before the rate limiter so the caller is known
}

app.UseRateLimiter();

var mcp = app.MapMcp("/mcp");
if (useEntra)
    mcp.RequireAuthorization(EntraAuthExtensions.ReadPolicy);

app.Run();
