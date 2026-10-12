# Entra ID authentication for the MCP server

The server acts as an OAuth 2.0 **resource server**: it never issues tokens, it only validates tokens issued by
Microsoft Entra ID. You register the server as an API, then register whatever calls it as a client.

```
Client (user or service) --1. get token--> Entra ID
Client --2. POST /mcp  Authorization: Bearer <JWT>--> MCP server
MCP server --3. validates signature, issuer, audience, expiry, scope/role--> runs the tool
```

Portal paths below are from memory of the Entra admin centre, which gets reorganised from time to time,
so adjust if a label has moved.

## 1. Register the API (the MCP server)

**Entra admin centre -> App registrations -> New registration**

- Name: `Trading MCP Server`
- Supported account types: **Single tenant**
- No redirect URI needed

Note the **Application (client) ID** and **Directory (tenant) ID**.

**Expose an API**
1. Set the Application ID URI to `api://<client-id>` (the default suggestion).
2. Add a scope named `mcp.read` ("Read trading data"), consent by **Admins and users**, state **Enabled**.

**Token version.** In the app's **Manifest**, set `requestedAccessTokenVersion` to `2` (under the `api` section
in the Microsoft Graph manifest format). Without this you get v1 tokens, whose issuer and audience
format don't match what the server expects.

**App roles** (for service-to-service callers, which have no signed-in user)
1. Create an app role: display name `Mcp.Read`, value `Mcp.Read`, allowed member types **Applications**, enabled.

## 2. Register a client

You need at least one client app that gets tokens for the API.

**A. Quick developer testing with Azure CLI (delegated, signed-in user)**
In the API registration, **Expose an API -> Add a client application**, add Azure CLI's well-known client ID
`04b07795-8ddb-461a-bbee-02f9e1bf7b46` and tick the `mcp.read` scope (this pre-authorises it). Then:

```bash
az login --tenant <tenant-id>
az account get-access-token --scope api://<api-client-id>/mcp.read --query accessToken -o tsv
```

**B. A service client (client credentials, no user)**
1. New registration `Trading MCP Client (daemon)`.
2. **Certificates & secrets -> New client secret** (use a certificate in production).
3. **API permissions -> Add a permission -> My APIs -> Trading MCP Server -> Application permissions -> `Mcp.Read`**,
   then **Grant admin consent**.
4. Get a token:

```bash
curl -s -X POST "https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token" \
  -d "grant_type=client_credentials" \
  -d "client_id=<daemon-client-id>" \
  -d "client_secret=<daemon-secret>" \
  -d "scope=api://<api-client-id>/.default"
```

Paste any token into https://jwt.ms to inspect it. Check:
`aud` is your API's client ID, `iss` ends in `/v2.0`, and there's either `scp` containing `mcp.read` (user tokens)
or `roles` containing `Mcp.Read` (service tokens).

## 3. Configure the server

```bash
cd McpServer
dotnet user-secrets set "Auth:Mode" "Entra"
dotnet user-secrets set "Entra:TenantId" "<tenant-id>"
dotnet user-secrets set "Entra:ClientId" "<api-client-id>"
dotnet run --urls http://localhost:5080
```

On App Service these are `Auth__Mode`, `Entra__TenantId` and `Entra__ClientId`.
Setting `Auth:Mode` back to `StaticTokens` returns to the simple bearer-token mode for local work.

## 4. Test it (a good on-camera sequence)

```bash
# No token -> 401, with a WWW-Authenticate header pointing at the protected-resource metadata
curl -i -X POST http://localhost:5080/mcp

# The metadata document that tells clients which authorization server to use
curl -s http://localhost:5080/.well-known/oauth-protected-resource

# Valid token -> the MCP endpoint answers (use MCP Inspector or Claude Code with the token as a header)

# Token for the wrong audience, an expired token, or a token without mcp.read / Mcp.Read -> 401 / 403
```

Then run a few tool calls and show the audit log. The `Caller` column now holds the Entra identity, for example
`alice@contoso.com [oid:...]` or `app:<client-id> [oid:...]`:

```sql
SELECT TOP 20 * FROM trading.AuditLog ORDER BY AuditId DESC;
```

## 5. What works today, and what doesn't

**Works well:** anything that can attach an `Authorization: Bearer` header, which covers service callers,
scripts, your own agents and clients that let you supply a token or custom headers.

**Interactive MCP clients are the hard part.** The MCP authorization flow expects the client to register itself
with the authorization server, normally through Dynamic Client Registration (DCR). **Entra ID doesn't offer DCR**,
so clients that insist on it can't complete the flow. Microsoft's own guidance says to preconfigure the client
with a client ID where DCR isn't available. What that means in practice:

- Clients that let you enter a pre-registered client ID can work, if you register them as a public client in Entra
  with the right redirect URIs.
- Some clients send an RFC 8707 `resource` parameter, which the Entra v2.0 endpoint has been reported to reject
  (`AADSTS901002`).
- I found at least one report of Claude's custom connectors failing against Entra for these reasons.
  **Test each client you plan to show before you promise it works on camera.**
- A common workaround is an authorization gateway in front of the server that handles client registration and
  token exchange. That's a good topic for a follow-up episode.

## 6. Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| 401, log says issuer invalid | Token is v1 (`iss` is `sts.windows.net`). Set `requestedAccessTokenVersion` to 2 in the manifest and fetch a new token. |
| 401, log says audience invalid | The token was requested for a different resource. The scope must be `api://<api-client-id>/...`. |
| 401, signature or key errors | Wrong `Entra:TenantId`, or the server can't reach `login.microsoftonline.com`. |
| 403 with a valid token | Token has neither `scp` = `mcp.read` nor `roles` = `Mcp.Read`. For service clients, check admin consent was granted. |
| Works in curl but not in a client | The client can't complete OAuth discovery or registration (see section 5). Supply the token directly instead. |

## 7. Production notes

- Use certificates, not client secrets, for service clients, or better, managed identities when the caller runs in Azure.
- Entra gives you Conditional Access, MFA, token lifetime policy, sign-in logs and instant revocation. Pair the
  server's audit table with Entra's sign-in logs for end-to-end traceability.
- Keep scopes narrow. If you later add write tools, give them a separate scope (for example `mcp.write`) and a
  separate policy, so read-only callers can never reach them.
- Consider restricting who can obtain tokens: **Enterprise applications -> Trading MCP Server -> Properties ->
  Assignment required**, then assign only the users and groups that need access.
