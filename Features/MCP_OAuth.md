# MCP OAuth Support for Remote Servers

Status: Proposed

---

## Motivation

Remote MCP servers (like Figma at `https://mcp.figma.com/mcp`) require OAuth 2.1 authentication. nb currently supports HTTP transport for remote MCP servers but has no authentication mechanism — connections to OAuth-protected servers fail silently or with an auth error.

The .NET MCP SDK v1.0+ has built-in OAuth support via `ClientOAuthOptions` on `HttpClientTransportOptions`, making this feasible without rolling our own OAuth implementation.

## How MCP OAuth Works

The MCP spec (2025-11-25) defines an OAuth 2.1 flow with PKCE:

1. Client connects to the MCP server endpoint
2. Server responds with 401 + `WWW-Authenticate` header pointing to its authorization server
3. Client discovers authorization server metadata (`.well-known/oauth-authorization-server`)
4. Client registers itself via Dynamic Client Registration (RFC 7591) or Client ID Metadata Document (CIMD)
5. Client opens browser for user to authorize
6. User authorizes, browser redirects to `http://localhost:{port}/callback` with auth code
7. Client exchanges auth code for access token
8. Client reconnects to MCP server with Bearer token
9. SDK handles token refresh automatically

The .NET MCP SDK handles steps 2-4 and 7-9 internally. We only need to provide:
- A `RedirectUri` (localhost callback URL)
- An `AuthorizationRedirectDelegate` (opens the browser)

## What the SDK Gives Us

The `ClientOAuthOptions` class on `HttpClientTransportOptions` provides:

```csharp
transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("https://mcp.figma.com/mcp"),
    OAuth = new ClientOAuthOptions
    {
        RedirectUri = new Uri("http://localhost:1179/callback"),
        AuthorizationRedirectDelegate = HandleAuthorizationUrlAsync,
        ClientName = "nb",
        // Scopes = ["mcp:connect"],  // Optional: override server-advertised scopes
    }
});
```

The SDK handles:
- Authorization server discovery (RFC 9728 Protected Resource Metadata)
- Client registration (CIMD preferred, DCR fallback)
- PKCE challenge generation
- Token exchange and refresh
- Token storage (needs investigation — may need to provide our own persistence)

## Implementation Plan

### Step 1: Upgrade MCP SDK (High Risk)

Upgrade `ModelContextProtocol` from `0.4.0-preview.2` to `1.0.x`.

**Known breaking changes:**
- Request methods now take a `RequestOptions` parameter instead of individual `JsonSerializerOptions`/`ProgressToken` parameters
- Various API surface changes across 0.5, 0.6, and 1.0 previews
- Need to audit all `McpManager.cs` call sites

**Risk:** This is the biggest risk area. The SDK went through significant API churn between 0.4 and 1.0. Every MCP interaction in `McpManager.cs` needs to be tested after upgrade.

### Step 2: Add OAuth Configuration to mcp.json

Extend `McpServerConfig` to support OAuth options:

```json
{
  "servers": {
    "figma": {
      "type": "http",
      "endpoint": "https://mcp.figma.com/mcp",
      "oauth": {
        "clientName": "nb",
        "scopes": ["mcp:connect"]
      }
    }
  }
}
```

The presence of an `oauth` block signals that the server requires authentication. Servers without it behave as today (unauthenticated HTTP).

### Step 3: Browser-Based Auth Flow

When the SDK triggers the `AuthorizationRedirectDelegate`:

1. Open the authorization URL in the user's default browser via `Process.Start(url, UseShellExecute=true)`
2. Spin up a temporary `HttpListener` on localhost to catch the callback
3. Extract the auth code from the redirect
4. Return it to the SDK (which handles the token exchange)

This is the same pattern used by `gh auth login`, `gcloud auth login`, `az login`, etc.

**UX consideration:** Show a Spectre.Console status message while waiting:
```
Waiting for browser authorization... (press Ctrl+C to cancel)
```

### Step 4: Token Persistence

The SDK may or may not persist tokens between sessions (needs investigation). If not, we need to store tokens so users don't re-authorize every time nb starts.

Likely location: `~/.nb/tokens/{server-name}.json` (encrypted or at minimum file-permission-protected).

**Open question:** Does the SDK provide a token cache interface, or do we need to implement `DelegatingHandler` / custom `HttpClient` to inject stored tokens?

## Blockers and Risks

### Figma Specifically Restricts Client Registration

This is the biggest concern for the Figma use case specifically. Figma does **not** allow arbitrary MCP clients to use Dynamic Client Registration. They maintain an allowlist of approved clients (Claude Code, Cursor, VS Code, Windsurf, Codex). Unknown clients get rejected at the DCR step.

**Options:**
1. **Apply for approval** — Figma has a form to request MCP client access. nb would need to be submitted and approved.
2. **Use a known client identity** — Not recommended; impersonating another client violates ToS.
3. **Wait for Figma to open up** — Community pressure exists (multiple forum threads requesting open access and PAT support).
4. **Focus on servers that allow open registration** — Other remote MCP servers may be more permissive. The OAuth implementation is still valuable for the ecosystem.

This means: **the OAuth feature is worth building for the MCP ecosystem generally, but Figma specifically may not work until they approve nb as a client.**

### SDK Upgrade Risk

The jump from 0.4.0-preview.2 to 1.0 is substantial. API signatures changed, new concepts were added (RequestOptions, CIMD), and some types may have moved namespaces. This should be done as an isolated PR with thorough testing of all existing MCP functionality before adding OAuth on top.

### Platform Differences

- **Linux:** `xdg-open` for browser launch; `HttpListener` may need permissions
- **macOS:** `open` for browser launch; straightforward
- **Windows:** `start` / `UseShellExecute`; straightforward
- Consider falling back to printing the URL if browser launch fails (common in headless/SSH scenarios)

## Suggested Phasing

### Phase 0: SDK Upgrade (Do First, Separately)
- Upgrade `ModelContextProtocol` to 1.0.x
- Fix all breaking changes in `McpManager.cs`
- Verify all existing MCP functionality still works (stdio servers, tools, prompts, resources)
- This is a standalone PR with no new features

### Phase 1: Basic OAuth Flow
- Add `ClientOAuthOptions` wiring in `McpManager.cs` for HTTP transports
- Browser launch + localhost callback listener
- OAuth config in `mcp.json`
- Test with a permissive remote MCP server (not Figma, unless approved)

### Phase 2: Token Persistence
- Store and reload tokens between sessions
- Handle token expiry / refresh failures gracefully
- Secure storage (file permissions, optional encryption)

### Phase 3: UX Polish
- Clear error messages for DCR rejection ("Server X rejected registration — it may require client approval")
- `/mcp auth <server>` command to re-authenticate
- `/mcp status` showing auth state per server
- Timeout handling for abandoned browser flows

## Testing Strategy

- **Unit:** Mock the OAuth endpoints to test the flow without a real server
- **Integration:** Use a local MCP server with OAuth enabled (the csharp-sdk repo has examples)
- **Manual:** Test against a real remote server that allows open DCR

## References

- [MCP C# SDK v1.0 release blog](https://devblogs.microsoft.com/dotnet/release-v10-of-the-official-mcp-csharp-sdk/)
- [OAuth in the MCP C# SDK (Den Delimarsky)](https://den.dev/blog/mcp-csharp-sdk-authorization/)
- [ClientOAuthOptions API docs](https://csharp.sdk.modelcontextprotocol.io/api/ModelContextProtocol.Authentication.ClientOAuthOptions.html)
- [MCP Authorization spec](https://modelcontextprotocol.io/specification/2025-03-26/basic/authorization)
- [Figma remote server setup](https://developers.figma.com/docs/figma-mcp-server/remote-server-installation/)
- [Figma DCR restriction discussion](https://forum.figma.com/report-a-problem-6/remote-mcp-server-oauth-client-registration-issue-45936)
- [Figma PAT request thread](https://forum.figma.com/ask-the-community-7/support-for-pat-personal-access-token-based-auth-in-figma-remote-mcp-47465)
