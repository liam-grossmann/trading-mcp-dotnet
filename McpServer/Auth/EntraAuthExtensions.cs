using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;

namespace TradingMcp.Auth;

/// <summary>
/// Microsoft Entra ID authentication for the MCP endpoint.
///
/// The server is an OAuth 2.0 *resource server*: it never issues tokens, it only validates them.
///   1. JwtBearer checks signature, issuer, audience and lifetime against Entra's published keys.
///   2. AddMcp(...) publishes OAuth Protected Resource Metadata (RFC 9728) and adds a
///      resource_metadata pointer to the WWW-Authenticate header on 401s, so MCP clients can
///      discover which authorization server to use.
///   3. An authorization policy requires the delegated scope (user tokens, "scp" claim)
///      or the app role (client-credentials tokens, "roles" claim).
/// </summary>
public static class EntraAuthExtensions
{
    public const string ReadPolicy = "McpRead";

    public static IServiceCollection AddEntraAuth(this IServiceCollection services, IConfiguration config)
    {
        var tenantId = Required(config, "Entra:TenantId");
        var clientId = Required(config, "Entra:ClientId");        // the API app registration's Application (client) ID
        var scope = config["Entra:RequiredScope"] ?? "mcp.read";   // delegated permission, for signed-in users
        var role = config["Entra:RequiredRole"] ?? "Mcp.Read";     // app role, for daemon/service clients

        // The v2.0 authority publishes the signing keys and the expected issuer.
        var authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.MapInboundClaims = false;   // keep raw claim names: scp, roles, oid, azp, preferred_username

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,            // issuer comes from the authority's metadata (single tenant)
                    ValidateAudience = true,          // the token must have been issued FOR this API
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidAudiences = [clientId, $"api://{clientId}"],
                    NameClaimType = "preferred_username",
                    RoleClaimType = "roles",
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            })
            .AddMcp(options =>
            {
                // NOTE: the property types on ResourceMetadata have changed between SDK versions
                // (Uri vs string). If this doesn't compile, let IntelliSense guide you, or compare
                // with the ProtectedMCPServer sample in the modelcontextprotocol/csharp-sdk repo.
                options.ResourceMetadata = new()
                {
                    // AuthorizationServers is a collection of strings in the current MCP SDK.
                    AuthorizationServers = { authority },
                    ScopesSupported = [$"api://{clientId}/{scope}"],
                };
            });

        services.AddAuthorization(options =>
            options.AddPolicy(ReadPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => HasScopeOrRole(ctx.User, scope, role))));

        return services;
    }

    private static bool HasScopeOrRole(ClaimsPrincipal user, string scope, string role)
    {
        var scopes = user.FindAll("scp")
                         .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return scopes.Contains(scope, StringComparer.OrdinalIgnoreCase)
            || user.FindAll("roles").Any(c => string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase));
    }

    private static string Required(IConfiguration config, string key) =>
        config[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{key} must be set when Auth:Mode is 'Entra'.");
}
