# Plugin_MCPServer

A [Genie4](https://github.com/GenieClient/Genie4) plugin that hosts a minimal [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) server inside the Genie client. AI clients (Copilot, Claude, etc.) can read DragonRealms game output, send commands, and query Genie variables.

The server speaks JSON-RPC 2.0 over HTTP POST (protocol version `2025-03-26`) and uses `System.Net.HttpListener`. There is no SSE/streaming support.

## Requirements

- .NET 10 (`net10.0-windows`)
- Genie4 with `Interfaces.dll` (referenced from the project directory)

## Build and install

1. Build the project: `dotnet build -c Release`
2. Copy the output `Plugin_MCPServer.dll` to Genie's `Plugins` folder.
3. Start Genie and load the plugin. It starts the server on load and echoes the listening address.

Use the plugin's `Show` action to echo the current endpoint again.

## Configuration

On first load the plugin sets these Genie variables if they are empty:

| Variable | Default | Description |
|---|---|---|
| `MCPServer.Host` | `localhost` | Host to bind. `0.0.0.0`, `*` or `+` bind all interfaces (may require admin rights/URL ACL on Windows). |
| `MCPServer.Port` | `8765` | TCP port. |
| `MCPServer.Endpoint` | `mcp` | URL path. |

Endpoint: `http://{Host}:{Port}/{Endpoint}/`, e.g. `http://localhost:8765/mcp/`.

Change a variable with `#var MCPServer.Port 9000`, then reload the plugin; the listener is only bound at startup.

## Tools

| Tool | Arguments | Description |
|---|---|---|
| `read_game_text` | `sinceId` (int, default 0), `maxLines` (int, default 100), `source` (`text`, `xml`, `all`; default `text`) | Returns buffered lines as `[id] text`, ending with `lastId=N`. Pass `lastId` as `sinceId` to poll for new output. |
| `send_command` | `command` (string, required) | Sends a command to the game as if typed. |
| `clear_game_text` | none | Clears the buffer. |
| `get_variable` | `name` (string, required) | Returns a Genie variable's value. |

Supported MCP methods: `initialize`, `ping`, `tools/list`, `tools/call`. Notifications are accepted (HTTP 202) and batches are supported.

## Behavior notes

- Game text (`ParseText`) and XML (`ParseXML`) are buffered in memory, up to the latest 2000 lines.
- Buffering stops when the plugin is disabled.
- There is no authentication. Keep the host on `localhost` unless you trust the network, since `send_command` can control your character.

## Client configuration

Visual Studio (`.mcp.json`) or VS Code (`.vscode/mcp.json`):

```json
{
  "servers": {
	"genie": {
	  "type": "http",
	  "url": "http://localhost:8765/mcp/"
	}
  }
}
```

## Testing

With Genie running and connected, ask your AI client to "read the latest game text" or "send the command `look`".

Or call the server directly:

```powershell
Invoke-RestMethod -Uri http://localhost:8765/mcp/ -Method Post -ContentType application/json -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```
