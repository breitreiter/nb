# MCP configuration

MCP servers are configured in `mcp.json`. Configuring a server makes it available; a
program still has to expose it with `mcp +name` before the model sees its tools.
Template: `mcp.example.json`.

```json
{
  "servers": {
    "my-server": {
      "type": "stdio",
      "command": "my-mcp-server",
      "args": ["--some-flag"],
      "alwaysAllow": ["tool1", "tool2"]
    }
  }
}
```

`alwaysAllow` lists tools that skip approval; `["*"]` auto-approves everything from a
server. Programs can also grant approval with `approval mcp <server>/<glob>`.

## Where `mcp.json` is read from

There is no `mcp.json` at the repo root. It resolves in layers, later winning by server
name: the executable's directory, then `~/.config/nb/mcp.json`, then the nearest
`.nb/mcp.json` walking up from the current directory. `--mcp <file>` uses a single
manifest and ignores the layers; `--mcp` with an empty manifest (`{"servers":{}}`) runs
with no servers at all, which is what a hermetic test wants.

nb exposes the current working directory as an MCP root, to help filesystem servers
orient themselves.

## HTTP servers and auth headers

Use `"type": "http"` with an `endpoint`. Auth goes in a `headers` object; values
support `${VAR}` interpolation against environment variables, so tokens stay out of the
committed file:

```json
"figma": {
  "type": "http",
  "endpoint": "https://mcp.figma.com/mcp",
  "headers": {
    "Authorization": "Bearer ${FIGMA_TOKEN}"
  }
}
```

Only values are interpolated, not keys. An unset variable logs a warning and resolves to
an empty string. Literal values work too.

## Inspecting the tool manifest

`nb --dump-tools` connects to the configured servers, writes the combined tool manifest
to `mcp-tools.json`, and exits. Useful when a server's tool names or schemas are not
what you expected.

## Built-in test server

`mcp-servers/mcp-tester/` is a self-contained C# MCP server with basic tools (echo,
reverse-echo, current-time) and markdown-driven prompts. It exercises the MCP path
without depending on a third-party server. The example manifest already has an entry
for it, running via `dotnet run --project`.
