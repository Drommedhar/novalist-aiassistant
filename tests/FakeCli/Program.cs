using System.Text.Json;

var mode = args[0];
var directory = args[1];
var transcript = Path.Combine(directory, "requests.jsonl");
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (mode == "claude")
{
    var prompt = await Console.In.ReadToEndAsync();
    var modelIndex = Array.IndexOf(args, "--model");
    var model = modelIndex < 0 ? "default" : args[modelIndex + 1];
    var systemIndex = Array.IndexOf(args, "--system-prompt-file");
    var system = systemIndex < 0 ? "" : await File.ReadAllTextAsync(args[systemIndex + 1]);
    await RecordAsync(new { method = "claude/prompt", model, prompt, system, arguments = args.Skip(2), processId = Environment.ProcessId });
    Write(new { type = "stream_event", @event = new { delta = new { type = "text_delta", text = model + ":" } } });
    await HoldAsync(prompt);
    Write(new { type = "stream_event", @event = new { delta = new { type = "text_delta", text = prompt } } });
    Write(new { type = "result", result = model + ":" + prompt });
    return;
}

var session = 0;
var selectedModel = "default";
while (await Console.In.ReadLineAsync() is { } line)
{
    using var request = JsonDocument.Parse(line);
    var root = request.RootElement;
    var method = root.GetProperty("method").GetString();
    await File.AppendAllTextAsync(transcript, line + Environment.NewLine);
    if (!root.TryGetProperty("id", out var requestId)) continue;
    var id = requestId.GetInt32();
    if (File.Exists(Path.Combine(directory, "hold-" + method?.Replace('/', '-')))) continue;
    if (method == "initialize") Reply(id, new { protocolVersion = 1 });
    else if (method == "session/new")
    {
        session++;
        selectedModel = "default";
        Reply(id, new { sessionId = "session-" + session, configOptions = new[] { new { category = "model", id = "config-" + session } } });
    }
    else if (method is "session/set_config_option" or "session/set_model")
    {
        if (File.Exists(Path.Combine(directory, "reject-model")))
            Write(new { jsonrpc = "2.0", id, error = new { code = -32602, message = "Model is unavailable" } });
        else if (method == "session/set_config_option" && File.Exists(Path.Combine(directory, "legacy-model")))
            Write(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found" } });
        else
        {
            selectedModel = root.GetProperty("params").GetProperty(method == "session/set_model" ? "modelId" : "value").GetString()!;
            Reply(id, new { });
        }
    }
    else if (method == "session/prompt")
    {
        var prompt = root.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString()!;
        if (prompt == "exit") return;
        if (session > 1) Chunk(session - 1, "STALE SESSION");
        Chunk(session, selectedModel + ":");
        await HoldAsync(prompt);
        Chunk(session, prompt);
        Reply(id, new { stopReason = "end_turn" });
    }
}

void Write(object value)
{
    Console.WriteLine(JsonSerializer.Serialize(value));
    Console.Out.Flush();
}
void Reply(int id, object result) => Write(new { jsonrpc = "2.0", id, result });
void Chunk(int sessionId, string text) => Write(new
{
    jsonrpc = "2.0", method = "session/update",
    @params = new { sessionId = "session-" + sessionId, update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } } }
});
Task RecordAsync(object value) => File.AppendAllTextAsync(transcript, JsonSerializer.Serialize(value) + Environment.NewLine);
async Task HoldAsync(string prompt)
{
    while (prompt.StartsWith("hold", StringComparison.Ordinal) && !File.Exists(Path.Combine(directory, "release")))
        await Task.Delay(10);
}
