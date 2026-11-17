using GeniePlugin.Interfaces;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Plugin_MCPServer;

/// <summary>
/// Genie4 plugin hosting a minimal MCP server (JSON-RPC over HTTP POST) that exposes game text and command sending.
/// Endpoint: http://{MCPServer.Host}:{MCPServer.Port}/{MCPServer.Endpoint} (defaults: localhost, 8765, mcp).
/// Use
/// </summary>
public class McpServerPlugin : IPlugin
{
    private const int MaxBufferLines = 2000;
    private const string ProtocolVersion = "2025-03-26";

    private IHost? _host;
    private bool _enabled = true;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    private readonly object _lock = new();
    private readonly List<(long Id, DateTime Time, string Source, string Text)> _buffer = new();
    private long _nextId = 1;

    public string Name => "MCP Server";
    public string Version => "0.1";
    public string Author => "Plugin_MCPServer";
    public string Description => "Runs an MCP server so AI clients can read DragonRealms game text and send commands.";

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public void Initialize(IHost host)
    {
        _host = host;
        if (string.IsNullOrEmpty(_host.get_Variable("MCPServer.Port")))
            _host.SendText("#var MCPServer.Port 8765");
        if (string.IsNullOrEmpty(_host.get_Variable("MCPServer.Host")))
            _host.SendText("#var MCPServer.Host localhost");
        if (string.IsNullOrEmpty(_host.get_Variable("MCPServer.Endpoint")))
            _host.SendText("#var MCPServer.Endpoint mcp");
        StartServer();
    }

    public void Show()
    {
        _host?.EchoText($"MCP Server listening at {Endpoint()}");
    }

    public void VariableChanged(string Variable) { }

    public string ParseInput(string Text) => Text;

    public string ParseText(string Text, string Window)
    {
        if (_enabled && !string.IsNullOrWhiteSpace(Text))
            AddLine("text", Text.TrimEnd('\r', '\n'));
        return Text;
    }

    public void ParseXML(string XML)
    {
        if (_enabled && !string.IsNullOrWhiteSpace(XML))
            AddLine("xml", XML.TrimEnd('\r', '\n'));
    }

    public void ParentClosing()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Close();
        }
        catch { }
    }

    private void AddLine(string source, string text)
    {
        lock (_lock)
        {
            _buffer.Add((_nextId++, DateTime.UtcNow, source, text));
            if (_buffer.Count > MaxBufferLines)
                _buffer.RemoveRange(0, _buffer.Count - MaxBufferLines);
        }
    }

    private int Port()
    {
        return int.TryParse(_host?.get_Variable("MCPServer.Port"), out var p) && p > 0 ? p : 8765;
    }

    private string EndpointPath()
    {
        var path = (_host?.get_Variable("MCPServer.Endpoint") ?? "").Trim('/', ' ');
        return path.Length == 0 ? "mcp" : path;
    }

    private string HostName()
    {
        var h = (_host?.get_Variable("MCPServer.Host") ?? "").Trim();
        if (h.Length == 0) return "localhost";
        return h is "0.0.0.0" or "*" or "+" ? "+" : h;
    }

    private string Endpoint() => $"http://{HostName()}:{Port()}/{EndpointPath()}/";

    private void StartServer()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(Endpoint());
            _listener.Start();
            _host?.EchoText($"MCP Server started at {Endpoint()}");
            _ = Task.Run(() => AcceptLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            _host?.EchoText("MCP Server failed to start: " + ex.Message);
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleRequest(ctx));
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                ctx.Response.StatusCode = 405;
                ctx.Response.Close();
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            JsonNode? root = JsonNode.Parse(body);
            JsonNode? result;
            if (root is JsonArray batch)
            {
                var arr = new JsonArray();
                foreach (var item in batch)
                {
                    var r = Process(item);
                    if (r != null) arr.Add(r);
                }
                result = arr.Count > 0 ? arr : null;
            }
            else
            {
                result = Process(root);
            }

            if (result == null)
            {
                ctx.Response.StatusCode = 202;
                ctx.Response.Close();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(result.ToJsonString());
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.StatusCode = 400; ctx.Response.Close(); } catch { }
        }
    }

    private JsonNode? Process(JsonNode? req)
    {
        if (req is not JsonObject obj) return Error(null, -32600, "Invalid request");
        var id = obj["id"]?.DeepClone();
        var method = obj["method"]?.GetValue<string>();
        var p = obj["params"] as JsonObject;

        if (id == null) return null; // notification

        try
        {
            switch (method)
            {
                case "initialize":
                    return Ok(id, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "Plugin_MCPServer", ["version"] = Version }
                    });
                case "ping":
                    return Ok(id, new JsonObject());
                case "tools/list":
                    return Ok(id, new JsonObject { ["tools"] = ToolList() });
                case "tools/call":
                    return Ok(id, CallTool(p?["name"]?.GetValue<string>(), p?["arguments"] as JsonObject));
                default:
                    return Error(id, -32601, "Method not found: " + method);
            }
        }
        catch (Exception ex)
        {
            return Error(id, -32603, ex.Message);
        }
    }

    private static JsonObject Tool(string name, string desc, JsonObject props, params string[] required)
    {
        var req = new JsonArray();
        foreach (var r in required) req.Add(r);
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = desc,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = req }
        };
    }

    private static JsonObject Prop(string type, string desc) => new() { ["type"] = type, ["description"] = desc };

    private static JsonArray ToolList() => new()
    {
        Tool("read_game_text",
            "Read buffered game output received from DragonRealms. Returns lines newer than sinceId (or the latest lines).",
            new JsonObject
            {
                ["sinceId"] = Prop("integer", "Only return lines with id greater than this. Default 0."),
                ["maxLines"] = Prop("integer", "Maximum lines to return (latest). Default 100."),
                ["source"] = Prop("string", "'text' (ParseText), 'xml' (ParseXML) or 'all'. Default 'text'.")
            }),
        Tool("send_command",
            "Send a command to the game as if typed by the user.",
            new JsonObject { ["command"] = Prop("string", "The command text to send.") },
            "command"),
        Tool("clear_game_text", "Clear the buffered game output.", new JsonObject()),
        Tool("get_variable", "Get a Genie variable value.",
            new JsonObject { ["name"] = Prop("string", "Variable name.") }, "name")
    };

    private JsonObject CallTool(string? name, JsonObject? args)
    {
        switch (name)
        {
            case "read_game_text":
                {
                    long since = args?["sinceId"]?.GetValue<long>() ?? 0;
                    int max = args?["maxLines"]?.GetValue<int>() ?? 100;
                    string source = args?["source"]?.GetValue<string>() ?? "text";
                    var sb = new StringBuilder();
                    long last = since;
                    lock (_lock)
                    {
                        var lines = _buffer.Where(l => l.Id > since && (source == "all" || l.Source == source)).ToList();
                        if (lines.Count > max) lines = lines.Skip(lines.Count - max).ToList();
                        foreach (var l in lines)
                        {
                            sb.Append('[').Append(l.Id).Append("] ");
                            if (source == "all") sb.Append('(').Append(l.Source).Append(") ");
                            sb.AppendLine(l.Text);
                            last = l.Id;
                        }
                    }
                    sb.Append("lastId=").Append(last);
                    return TextResult(sb.ToString());
                }
            case "send_command":
                {
                    var cmd = args?["command"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(cmd)) return TextResult("command is required", true);
                    if (_host == null) return TextResult("Host not initialized", true);
                    _host.SendText(cmd);
                    return TextResult("Sent: " + cmd);
                }
            case "clear_game_text":
                lock (_lock) _buffer.Clear();
                return TextResult("Cleared");
            case "get_variable":
                {
                    var v = args?["name"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(v) || _host == null) return TextResult("name is required", true);
                    return TextResult(_host.get_Variable(v) ?? "");
                }
            default:
                return TextResult("Unknown tool: " + name, true);
        }
    }

    private static JsonObject TextResult(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError
    };

    private static JsonObject Ok(JsonNode id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
}
