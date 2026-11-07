using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Velocity.Engine.Persist;
using Velocity.Engine.Tools;
using Xunit;

namespace Velocity.Engine.Tests;

public class ManagerTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDir Dir = new();
        public readonly ConcurrentQueue<(string Name, JsonNode? Data)> Events = new();
        public Manager Manager = null!;

        public string Data => Path.Combine(Dir.Path, "data");
        public string Downloads => Path.Combine(Dir.Path, "dl");

        public Rig()
        {
            Directory.CreateDirectory(Downloads);
            Open();
        }

        public void Open()
        {
            Manager = new Manager(Data, (name, data) => Events.Enqueue((name, data)));
            Manager.ApplySettings(new JsonObject { ["folder"] = Downloads });
            Manager.Start();
        }

        public async Task<JsonObject> WaitStatus(string id, string status, int ms = 20000)
        {
            var started = Environment.TickCount64;
            while (Environment.TickCount64 - started < ms)
            {
                var found = Manager.ListItems().FirstOrDefault(i => i?["id"]?.GetValue<string>() == id) as JsonObject;
                if (found?["status"]?.GetValue<string>() == status) return found;
                await Task.Delay(50);
            }
            throw new Xunit.Sdk.XunitException($"status {status} não chegou");
        }

        public string Add(Uri url, int priority = 1)
        {
            var added = (JsonArray)Manager.Add(new JsonObject { ["urls"] = new JsonArray((JsonNode)url.ToString()), ["priority"] = priority });
            return added[0]!["id"]!.GetValue<string>();
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            Dir.Dispose();
        }
    }

    [Fact(Timeout = 30000)]
    public async Task DownloadEndsInHistoryWithoutSecrets()
    {
        await using var server = new TestServer(new ServerOptions { Size = 1024 * 1024 });
        await using var rig = new Rig();
        var id = rig.Add(new Uri(server.Url() + "?token=SEGREDO123"));
        var done = await rig.WaitStatus(id, "done");
        await TestServer.AssertContentAsync(done["finalPath"]!.GetValue<string>(), 1024 * 1024);
        var history = (JsonArray)rig.Manager.History(10);
        Assert.Single(history);
        Assert.DoesNotContain("SEGREDO123", history.ToJsonString());
        Assert.DoesNotContain("SEGREDO123", done.ToJsonString());
        await rig.Manager.DisposeAsync();
        var queue = await File.ReadAllTextAsync(Path.Combine(rig.Data, "queue.json"));
        Assert.DoesNotContain("SEGREDO123", queue);
        Assert.DoesNotContain("SEGREDO123", await File.ReadAllTextAsync(Path.Combine(rig.Data, "history.jsonl")));
        Assert.Contains(rig.Events, e => e.Name == "item" && e.Data?["status"]?.GetValue<string>() == "done");
        Assert.DoesNotContain(rig.Events, e => e.Data?.ToJsonString().Contains("SEGREDO123") == true);
        rig.Open();
    }

    [Fact(Timeout = 30000)]
    public async Task QueueSurvivesRestartAndResumes()
    {
        const long size = 16 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 4 * 1024 * 1024 });
        await using var rig = new Rig();
        var id = rig.Add(server.Url());
        await rig.WaitStatus(id, "running");
        while (((JsonObject)rig.Manager.ListItems()[0]!)["done"]!.GetValue<long>() < 4 * 1024 * 1024) await Task.Delay(30);
        await rig.Manager.DisposeAsync();
        var bytesBefore = server.TotalBytes;
        rig.Open();
        var done = await rig.WaitStatus(id, "done");
        await TestServer.AssertContentAsync(done["finalPath"]!.GetValue<string>(), size);
        Assert.True(server.TotalBytes - bytesBefore < size, "baixou tudo de novo");
    }

    [Fact(Timeout = 30000)]
    public async Task PauseResumeAndCancelCleanUp()
    {
        const long size = 32 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 2 * 1024 * 1024 });
        await using var rig = new Rig();
        var id = rig.Add(server.Url());
        await rig.WaitStatus(id, "running");
        await Task.Delay(400);
        rig.Manager.Pause(id);
        var paused = await rig.WaitStatus(id, "paused");
        Assert.True(paused["done"]!.GetValue<long>() > 0);
        Assert.Single(Directory.GetFiles(rig.Downloads, "*.vpart"));
        rig.Manager.Resume(id);
        await rig.WaitStatus(id, "running");
        await Task.Delay(300);
        rig.Manager.Cancel(id);
        var started = Environment.TickCount64;
        while (rig.Manager.ListItems().Count > 0 && Environment.TickCount64 - started < 5000) await Task.Delay(30);
        Assert.Empty(rig.Manager.ListItems());
        Assert.Empty(Directory.GetFiles(rig.Downloads));
    }

    [Fact(Timeout = 30000)]
    public async Task HighPriorityPreemptsLowWhenSlotsAreFull()
    {
        const long size = 32 * 1024 * 1024;
        await using var slow = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 1024 * 1024 });
        await using var fast = new TestServer(new ServerOptions { Size = 1024 * 1024 });
        await using var rig = new Rig();
        rig.Manager.ApplySettings(new JsonObject { ["maxConcurrent"] = 1 });
        var low = rig.Add(slow.Url(), 2);
        await rig.WaitStatus(low, "running");
        var high = rig.Add(fast.Url(), 0);
        await rig.WaitStatus(high, "done");
        await rig.WaitStatus(low, "running");
        rig.Manager.Cancel(low);
    }

    [Fact(Timeout = 30000)]
    public async Task ReorderingChangesWhoStartsFirst()
    {
        await using var a = new TestServer(new ServerOptions { Size = 8 * 1024 * 1024, PerConnectionBps = 4 * 1024 * 1024 });
        await using var b = new TestServer(new ServerOptions { Size = 1024 });
        await using var c = new TestServer(new ServerOptions { Size = 1024 });
        await using var rig = new Rig();
        rig.Manager.ApplySettings(new JsonObject { ["maxConcurrent"] = 1 });
        var first = rig.Add(a.Url());
        await rig.WaitStatus(first, "running");
        var second = rig.Add(b.Url());
        var third = rig.Add(c.Url());
        rig.Manager.Move(third, "up");
        var ids = rig.Manager.ListItems().Select(i => i!["id"]!.GetValue<string>()).ToList();
        Assert.True(ids.IndexOf(third) < ids.IndexOf(second));
        rig.Manager.Cancel(first);
        await rig.WaitStatus(third, "done");
    }

    [Fact(Timeout = 30000)]
    public async Task RejectsBadInputAndClassifiesLinks()
    {
        await using var rig = new Rig();
        Assert.Throws<EngineError>(() => Manager.Classify("javascript:alert(1)"));
        Assert.Throws<EngineError>(() => Manager.Classify("file:///C:/Windows/win.ini"));
        Assert.Throws<EngineError>(() => Manager.Classify("não é link"));
        Assert.Throws<EngineError>(() => Manager.Classify("ftp://x/y"));
        Assert.Equal("torrent", Manager.Classify("magnet:?xt=urn:btih:abc").Kind);
        Assert.Equal("torrent", Manager.Classify("https://x.org/a.torrent").Kind);
        Assert.Equal("http", Manager.Classify("https://x.org/a.zip").Kind);
        Assert.Throws<EngineError>(() => rig.Manager.Add(new JsonObject { ["urls"] = new JsonArray((JsonNode)"http://x/y"), ["folder"] = "relativa\\pasta" }));
        Assert.Throws<EngineError>(() => Manager.NormalizeChecksum("abc"));
        Assert.Equal("sha256:" + new string('a', 64), Manager.NormalizeChecksum(new string('A', 64)));
    }

    [Fact(Timeout = 30000)]
    public async Task InspectDescribesLinksWithoutLeakingQuery()
    {
        await using var server = new TestServer(new ServerOptions { Size = 2048 });
        await using var rig = new Rig();
        var result = (JsonArray)await rig.Manager.InspectAsync(new JsonObject { ["urls"] = new JsonArray((JsonNode)(server.Url() + "?token=SEGREDO"), (JsonNode)server.Url("/missing").ToString(), (JsonNode)"magnet:?xt=urn:btih:abc&dn=Linux") }, CancellationToken.None);
        Assert.True(result[0]!["ok"]!.GetValue<bool>());
        Assert.Equal(2048, result[0]!["size"]!.GetValue<long>());
        Assert.Equal("arquivo.bin", result[0]!["name"]!.GetValue<string>());
        Assert.DoesNotContain("SEGREDO", result.ToJsonString());
        Assert.False(result[1]!["ok"]!.GetValue<bool>());
        Assert.Equal("Linux", result[2]!["name"]!.GetValue<string>());
    }

    [Fact(Timeout = 30000)]
    public async Task SecretsRoundTripOnlyThroughDpapi()
    {
        await Task.Yield();
        var stored = Secrets.Protect("https://x/?t=abc");
        Assert.DoesNotContain("abc", stored);
        Assert.Equal("https://x/?t=abc", Secrets.Unprotect(stored));
        Assert.Null(Secrets.Unprotect("não é base64"));
    }

    [Fact(Timeout = 30000)]
    public async Task SettingsAreValidated()
    {
        await Task.Yield();
        var settings = new Settings();
        settings.Apply(new JsonObject { ["mode"] = "qualquer", ["maxConcurrent"] = 9999, ["folder"] = "relativa", ["advanced"] = new JsonObject { ["maxConnections"] = 5000, ["minFragmentKiB"] = 1 } });
        Assert.Equal("auto", settings.Mode);
        Assert.Equal(16, settings.MaxConcurrent);
        Assert.Equal("", settings.Folder);
        Assert.Equal(64, settings.Advanced.MaxConnections);
        Assert.Equal(64, settings.Advanced.MinFragmentKiB);
    }

    [Fact(Timeout = 30000)]
    public async Task YtDlpArgumentsCannotInjectOptions()
    {
        await Task.Yield();
        var args = YtDlpJob.BuildArguments("--exec=calc", "C:\\pasta", "auto", null);
        Assert.Equal("--", args[^2]);
        Assert.Equal("--exec=calc", args[^1]);
        Assert.Contains("--ignore-config", args);
        Assert.Contains("--no-exec", args);
    }
}
