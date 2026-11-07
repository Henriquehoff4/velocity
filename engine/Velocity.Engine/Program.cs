using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Velocity.Engine;
using Velocity.Engine.Net;
using Velocity.Engine.Persist;
using Velocity.Engine.Tools;

var dataDir = "";
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--data") dataDir = args[i + 1];
if (dataDir.Length == 0) dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Velocity", "engine");

ToolFinder.ToolsDir = Environment.GetEnvironmentVariable("VELOCITY_TOOLS");
if (Environment.GetEnvironmentVariable("VELOCITY_TEST") == "1")
{
    var addresses = Environment.GetEnvironmentVariable("VELOCITY_TEST_LINKS");
    if (!string.IsNullOrEmpty(addresses))
        NetworkMonitor.TestAddresses = addresses.Split(',').Select(System.Net.IPAddress.Parse).ToArray();
}

var output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 65536) { AutoFlush = false };
var writer = Task.Run(async () =>
{
    while (await output.Reader.WaitToReadAsync())
    {
        while (output.Reader.TryRead(out var line))
        {
            await stdout.WriteAsync(line);
            await stdout.WriteAsync('\n');
        }
        await stdout.FlushAsync();
    }
});

void Emit(string name, JsonNode? data)
{
    var message = new JsonObject { ["event"] = name, ["data"] = data };
    output.Writer.TryWrite(message.ToJsonString());
}

await using var manager = new Manager(dataDir, Emit);
manager.Start();

using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var running = new List<Task>();
while (await input.ReadLineAsync() is { } line)
{
    if (line.Length == 0) continue;
    JsonObject? request;
    try
    {
        request = JsonNode.Parse(line) as JsonObject;
    }
    catch (System.Text.Json.JsonException)
    {
        continue;
    }
    if (request == null) continue;
    var id = Json.Long(request, "id", -1);
    var command = Json.Str(request, "cmd");
    var arguments = request["args"] as JsonObject;
    request.Remove("args");
    if (command == "shutdown") break;
    running.RemoveAll(t => t.IsCompleted);
    running.Add(Task.Run(async () =>
    {
        var reply = new JsonObject { ["id"] = id };
        try
        {
            reply["data"] = await Dispatch(command, arguments);
            reply["ok"] = true;
        }
        catch (EngineError error)
        {
            reply["ok"] = false;
            reply["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message };
        }
        catch (HttpFailure failure)
        {
            reply["ok"] = false;
            reply["error"] = new JsonObject { ["code"] = failure.Code, ["message"] = failure.Message };
        }
        catch (HttpRequestException)
        {
            reply["ok"] = false;
            reply["error"] = new JsonObject { ["code"] = "network", ["message"] = "Não foi possível conectar ao servidor." };
        }
        catch (TaskCanceledException)
        {
            reply["ok"] = false;
            reply["error"] = new JsonObject { ["code"] = "timeout", ["message"] = "O servidor demorou demais para responder." };
        }
        catch (Exception e)
        {
            reply["ok"] = false;
            reply["error"] = new JsonObject { ["code"] = "internal", ["message"] = $"Erro interno do motor: {e.GetType().Name}" };
        }
        output.Writer.TryWrite(reply.ToJsonString());
    }));
}

await manager.DisposeAsync();
output.Writer.TryComplete();
await writer;

async Task<JsonNode?> Dispatch(string command, JsonObject? a)
{
    var id = Json.Str(a, "id");
    switch (command)
    {
        case "list": return manager.ListItems();
        case "inspect": return await manager.InspectAsync(a, CancellationToken.None);
        case "add": return manager.Add(a);
        case "pause": manager.Pause(id); return null;
        case "resume": manager.Resume(id); return null;
        case "cancel": manager.Cancel(id); return null;
        case "remove": manager.Remove(id); return null;
        case "pauseAll": manager.PauseAll(); return null;
        case "resumeAll": manager.ResumeAll(); return null;
        case "clearDone": manager.ClearDone(); return null;
        case "priority": manager.SetPriority(id, (int)Json.Long(a, "priority", 1)); return null;
        case "move": manager.Move(id, Json.Str(a, "direction")); return null;
        case "reorder": manager.Reorder(a); return null;
        case "updateLink": await manager.UpdateLinkAsync(a, CancellationToken.None); return null;
        case "setAuth": manager.SetAuth(a); return null;
        case "watch": manager.Watch(id); return null;
        case "settings.get": return manager.CurrentSettings.ToJson();
        case "settings.set": return manager.ApplySettings(a);
        case "net.list": return manager.NetSummary();
        case "net.checkPublic": return await manager.CheckPublicAsync(CancellationToken.None);
        case "history.list": return manager.History((int)Json.Long(a, "limit", 200));
        case "history.clear": manager.ClearHistory(); return null;
        case "tools": return manager.Tools();
        default: throw new EngineError("unknown-command", "Comando desconhecido.");
    }
}
