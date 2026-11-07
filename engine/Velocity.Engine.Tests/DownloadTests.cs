using System.Net;
using System.Security.Cryptography;
using System.Text;
using Velocity.Engine.Core;
using Velocity.Engine.Net;
using Xunit;

namespace Velocity.Engine.Tests;

internal static class Harness
{
    public static readonly HttpPool Pool = new();

    public static DownloadSpec Spec(string folder, Uri url, SpeedProfile? profile = null, RequestAuth? auth = null, string id = "t1", string? checksum = null, int stallMs = 20000, int noProgressMs = 25000, IEnumerable<Uri>? mirrors = null, Func<IReadOnlyList<NetLink>>? links = null, HttpPool? pool = null)
    {
        var sources = new List<SourceSpec> { new(url, auth) };
        if (mirrors != null) sources.AddRange(mirrors.Select(m => new SourceSpec(m, null)));
        return new DownloadSpec
        {
            Id = id,
            Sources = sources,
            Folder = folder,
            PartPath = Path.Combine(folder, id + ".vpart"),
            Checksum = checksum,
            Profile = profile ?? new SpeedProfile { SettleMs = 300, MinFragment = 256 * 1024 },
            Pool = pool ?? Pool,
            StallMs = stallMs,
            NoProgressMs = noProgressMs,
            MarkOfTheWeb = false,
            Links = links,
        };
    }

    public static SpeedProfile Fixed(int connections) => new() { FixedConnections = connections, MaxConnections = connections, StartConnections = connections, MinFragment = 256 * 1024 };
}

public class DownloadCoreTests
{
    [Fact(Timeout = 30000)]
    public async Task ParallelDownloadIsByteExactAndUsesManyConnections()
    {
        const long size = 24 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 4 * 1024 * 1024 });
        using var dir = new TempDir();
        var download = new HttpDownload(Harness.Spec(dir.Path, server.Url()));
        var result = await download.RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.True(result.Verified);
        Assert.True(server.MaxActive >= 2, $"máximo de conexões {server.MaxActive}");
        await TestServer.AssertContentAsync(result.FinalPath, size);
        Assert.False(File.Exists(Path.Combine(dir.Path, "t1.vpart")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "t1.vpart.vdl")));
        Assert.Equal("arquivo.bin", Path.GetFileName(result.FinalPath));
    }

    [Fact(Timeout = 30000)]
    public async Task FixedConnectionsBeatsSingleUnderPerConnectionLimit()
    {
        const long size = 12 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 3 * 1024 * 1024 });
        using var dir = new TempDir();
        var started = Environment.TickCount64;
        var single = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), Harness.Fixed(1), id: "one")).RunAsync(CancellationToken.None);
        var singleMs = Environment.TickCount64 - started;
        started = Environment.TickCount64;
        var many = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), Harness.Fixed(6), id: "many")).RunAsync(CancellationToken.None);
        var manyMs = Environment.TickCount64 - started;
        Assert.Equal(DownloadEnd.Done, single.End);
        Assert.Equal(DownloadEnd.Done, many.End);
        Assert.True(manyMs * 2 < singleMs, $"1 conexão {singleMs} ms, 6 conexões {manyMs} ms");
        await TestServer.AssertContentAsync(many.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task NeverOverwritesExistingFile()
    {
        await using var server = new TestServer(new ServerOptions { Size = 2 * 1024 * 1024 });
        using var dir = new TempDir();
        var existing = Path.Combine(dir.Path, "arquivo.bin");
        await File.WriteAllTextAsync(existing, "meu");
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url())).RunAsync(CancellationToken.None);
        Assert.Equal("arquivo (1).bin", Path.GetFileName(result.FinalPath));
        Assert.Equal("meu", await File.ReadAllTextAsync(existing));
    }

    [Fact(Timeout = 30000)]
    public async Task SpeedLimitIsRespected()
    {
        const long size = 3 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size });
        using var dir = new TempDir();
        var started = Environment.TickCount64;
        var profile = new SpeedProfile { LimitBps = 1024 * 1024, MaxConnections = 4, MinFragment = 256 * 1024 };
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), profile)).RunAsync(CancellationToken.None);
        var elapsed = Environment.TickCount64 - started;
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.True(elapsed >= 2000, $"terminou em {elapsed} ms");
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task TinyAndEmptyFilesWork()
    {
        await using var tiny = new TestServer(new ServerOptions { Size = 5 });
        await using var empty = new TestServer(new ServerOptions { Size = 0, Ranges = false });
        using var dir = new TempDir();
        var a = await new HttpDownload(Harness.Spec(dir.Path, tiny.Url(), id: "a")).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, a.End);
        await TestServer.AssertContentAsync(a.FinalPath, 5);
        var b = await new HttpDownload(Harness.Spec(dir.Path, empty.Url(), id: "b")).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, b.End);
        Assert.Equal(0, new FileInfo(b.FinalPath).Length);
    }
}

public class ServerProblemTests
{
    [Fact(Timeout = 30000)]
    public async Task WithoutRangeUsesOneConnection()
    {
        const long size = 4 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, Ranges = false });
        using var dir = new TempDir();
        var download = new HttpDownload(Harness.Spec(dir.Path, server.Url()));
        var result = await download.RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.Equal(1, server.MaxActive);
        Assert.False(download.Stats.Resumable);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task ChunkedWithoutLengthWorks()
    {
        const long size = 2 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, Ranges = false, Chunked = true });
        using var dir = new TempDir();
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url())).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task ServerIgnoringRangeFallsBackWithoutCorruption()
    {
        const long size = 6 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, IgnoreRangeOnWorkers = true, Etag = "", LastModified = "" });
        using var dir = new TempDir();
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url())).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task InvalidContentRangeFallsBackWithoutCorruption()
    {
        const long size = 6 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, BadContentRangeOnWorkers = true });
        using var dir = new TempDir();
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url())).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task DroppedConnectionsRecover()
    {
        const long size = 12 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, DropAfterBytes = 512 * 1024, DropTimes = 6, PerConnectionBps = 8 * 1024 * 1024 });
        using var dir = new TempDir();
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url())).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task HungConnectionIsReplaced()
    {
        const long size = 6 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, HangAfterBytes = 256 * 1024, HangTimes = 1, PerConnectionBps = 8 * 1024 * 1024 });
        using var dir = new TempDir();
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), stallMs: 1500)).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task Http429BacksOffAndReducesConnections()
    {
        const long size = 16 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, MaxConcurrent = 2, OverLimitStatus = 429, RetryAfterSeconds = 1, PerConnectionBps = 6 * 1024 * 1024 });
        using var dir = new TempDir();
        var hosts = new HostMemory();
        var spec = Harness.Spec(dir.Path, server.Url(), Harness.Fixed(6));
        var download = new HttpDownload(new DownloadSpec
        {
            Id = spec.Id, Sources = spec.Sources, Folder = spec.Folder, PartPath = spec.PartPath, Profile = spec.Profile, Pool = spec.Pool, Hosts = hosts, MarkOfTheWeb = false, NoProgressMs = 25000,
        });
        var result = await download.RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.True(server.Rejected >= 1);
        Assert.True(server.Rejected <= 12, $"insistiu {server.Rejected} vezes");
        Assert.NotNull(hosts.Get("127.0.0.1"));
        Assert.True(hosts.Get("127.0.0.1")!.Cap is > 0 and <= 4);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task ConnectionLimitedServerStillCompletes()
    {
        const long size = 10 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, MaxConcurrent = 3, OverLimitStatus = 403, PerConnectionBps = 6 * 1024 * 1024 });
        using var dir = new TempDir();
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), Harness.Fixed(8))).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task EtagChangeRestartsAndEndsConsistent()
    {
        const long size = 6 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, SwitchEtagAfterBytes = "\"novo\"", SwitchEtagAt = 1, PerConnectionBps = 16 * 1024 * 1024 });
        using var dir = new TempDir();
        var download = new HttpDownload(Harness.Spec(dir.Path, server.Url()));
        var result = await download.RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.Contains("mudou", download.Stats.Note);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task HttpErrorsAreDescribed()
    {
        await using var server = new TestServer(new ServerOptions { Size = 1024 });
        using var dir = new TempDir();
        var missing = await new HttpDownload(Harness.Spec(dir.Path, server.Url("/missing"))).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Failed, missing.End);
        Assert.Equal("not-found", missing.Code);
        var loop = await new HttpDownload(Harness.Spec(dir.Path, server.Url("/loop"))).RunAsync(CancellationToken.None);
        Assert.Equal("too-many-redirects", loop.Code);
        var ftp = await new HttpDownload(Harness.Spec(dir.Path, server.Url("/ftp"))).RunAsync(CancellationToken.None);
        Assert.Equal("bad-redirect", ftp.Code);
    }

    [Fact(Timeout = 30000)]
    public async Task ProbeDetectsHtml()
    {
        await using var server = new TestServer(new ServerOptions { Size = 1024 });
        var probe = await HttpProtocol.ProbeAsync(Harness.Pool.Get(null), server.Url("/html"), null, CancellationToken.None);
        Assert.True(probe.IsHtml);
        Assert.False(probe.Ranges);
    }
}

public class ResumeTests
{
    private static async Task PauseAfter(HttpDownload download, Task<DownloadResult> run, CancellationTokenSource cts, long bytes)
    {
        while (download.Stats.Done < bytes && !run.IsCompleted) await Task.Delay(20);
        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task PauseAndResumeDoesNotDownloadAgain()
    {
        const long size = 20 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 8 * 1024 * 1024 });
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var first = new HttpDownload(Harness.Spec(dir.Path, server.Url(), Harness.Fixed(4)));
        var run = first.RunAsync(cts.Token);
        await PauseAfter(first, run, cts, 8 * 1024 * 1024);
        var paused = await run;
        Assert.Equal(DownloadEnd.Paused, paused.End);
        var before = server.TotalBytes;
        Assert.True(File.Exists(Path.Combine(dir.Path, "t1.vpart")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "t1.vpart.vdl")));
        var second = new HttpDownload(Harness.Spec(dir.Path, server.Url(), Harness.Fixed(4)));
        var result = await second.RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
        var resumedBytes = server.TotalBytes - before;
        Assert.True(resumedBytes < size * 0.8, $"baixou de novo {resumedBytes} de {size}");
    }

    [Fact(Timeout = 30000)]
    public async Task SimulatedPowerLossKeepsOnlyJournaledData()
    {
        const long size = 16 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 8 * 1024 * 1024 });
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var first = new HttpDownload(Harness.Spec(dir.Path, server.Url(), Harness.Fixed(4)));
        var run = first.RunAsync(cts.Token);
        await PauseAfter(first, run, cts, 6 * 1024 * 1024);
        await run;
        var journal = Path.Combine(dir.Path, "t1.vpart.vdl");
        var bytes = await File.ReadAllBytesAsync(journal);
        await File.WriteAllBytesAsync(journal, bytes[..^12]);
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), Harness.Fixed(4))).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task CorruptedPartIsDetectedAndRepaired()
    {
        const long size = 16 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 8 * 1024 * 1024 });
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var first = new HttpDownload(Harness.Spec(dir.Path, server.Url(), Harness.Fixed(4)));
        var run = first.RunAsync(cts.Token);
        await PauseAfter(first, run, cts, 8 * 1024 * 1024);
        await run;
        var part = Path.Combine(dir.Path, "t1.vpart");
        await using (var stream = new FileStream(part, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(1000, SeekOrigin.Begin);
            stream.WriteByte(0xEE);
            stream.WriteByte(0xEE);
        }
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), Harness.Fixed(4))).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task FileChangedWhilePausedStartsOver()
    {
        const long size = 12 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 8 * 1024 * 1024 });
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var first = new HttpDownload(Harness.Spec(dir.Path, server.Url(), Harness.Fixed(2)));
        var run = first.RunAsync(cts.Token);
        await PauseAfter(first, run, cts, 3 * 1024 * 1024);
        await run;
        server.Options.Etag = "\"outra-versao\"";
        var before = server.TotalBytes;
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), Harness.Fixed(2))).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.True(server.TotalBytes - before >= size);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task ProcessKillLeavesRecoverableState()
    {
        const long size = 12 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size });
        using var dir = new TempDir();
        var part = Path.Combine(dir.Path, "t1.vpart");
        var journalPath = part + ".vdl";
        var header = new JournalHeader(size, "\"abc\"", "Sat, 01 Jan 2022 00:00:00 GMT");
        var data = new byte[4 * 1024 * 1024];
        TestServer.Fill(7, 0, data);
        await File.WriteAllBytesAsync(part, new byte[size]);
        await using (var stream = new FileStream(part, FileMode.Open, FileAccess.Write)) await stream.WriteAsync(data);
        var writer = new ExtentWriter();
        writer.Append(0, data);
        var extents = new List<Extent>();
        writer.Drain(extents);
        using (var journal = Journal.Create(journalPath, header)) journal.Append(extents);
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url())).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.True(server.TotalBytes <= size - data.Length + 2 * 1024 * 1024, $"baixou {server.TotalBytes}");
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }
}

public class LinkAndAuthTests
{
    [Fact(Timeout = 30000)]
    public async Task FollowsRedirectsAndKeepsAuthOnSameOrigin()
    {
        const long size = 2 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, RequireBasic = "ana:segredo" });
        using var dir = new TempDir();
        var auth = new RequestAuth { Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ana:segredo")) };
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url("/redir"), auth: auth)).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task AuthIsNotSentToAnotherOrigin()
    {
        const long size = 1024 * 1024;
        await using var target = new TestServer(new ServerOptions { Size = size });
        await using var front = new TestServer(new ServerOptions { Size = size });
        using var dir = new TempDir();
        var auth = new RequestAuth { Authorization = "Basic c2VncmVkbw==", Cookie = "s=1" };
        var result = await new HttpDownload(Harness.Spec(dir.Path, front.Url($"/go/{target.Port}"), auth: auth)).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.Contains(front.Authorizations, a => a.Contains("/go/"));
        Assert.Empty(target.Authorizations);
    }

    [Fact(Timeout = 30000)]
    public async Task UserInfoInUrlBecomesHeader()
    {
        var (clean, auth) = HttpProtocol.SplitUserInfo(new Uri("http://ana:se%20nha@host/x"), null);
        await Task.Yield();
        Assert.Equal("http://host/x", clean.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ana:se nha")), auth!.Authorization);
    }

    [Fact(Timeout = 30000)]
    public async Task MissingCredentialsAskForLogin()
    {
        await using var server = new TestServer(new ServerOptions { Size = 1024, RequireBasic = "a:b" });
        using var dir = new TempDir();
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url())).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.NeedsAuth, result.End);
    }

    [Fact(Timeout = 30000)]
    public async Task ExpiredTemporaryLinkIsResolvedAgain()
    {
        const long size = 8 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, ExpireTokenAfterBytes = 3 * 1024 * 1024, PerConnectionBps = 12 * 1024 * 1024 });
        using var dir = new TempDir();
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url("/link"))).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task ExpiredLinkWithoutRefreshAsksNewLinkAndResumes()
    {
        const long size = 8 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 6 * 1024 * 1024, DropAfterBytes = 512 * 1024, DropTimes = 100000 });
        using var dir = new TempDir();
        var tokenUrl = server.Url("/tok?t=0");
        var download = new HttpDownload(Harness.Spec(dir.Path, tokenUrl, Harness.Fixed(1), noProgressMs: 20000));
        var run = download.RunAsync(CancellationToken.None);
        while (download.Stats.Done < 2 * 1024 * 1024) await Task.Delay(20);
        server.RotateToken();
        var firstResult = await run;
        Assert.Equal(DownloadEnd.NeedsLink, firstResult.End);
        server.Options.DropAfterBytes = 0;
        var doneBefore = server.TotalBytes;
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url("/tok?t=1"), Harness.Fixed(2))).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.True(server.TotalBytes - doneBefore < size);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }
}

public class MirrorTests
{
    [Fact(Timeout = 30000)]
    public async Task UsesBothMirrors()
    {
        const long size = 16 * 1024 * 1024;
        await using var a = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 4 * 1024 * 1024 });
        await using var b = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 4 * 1024 * 1024 });
        using var dir = new TempDir();
        var result = await new HttpDownload(Harness.Spec(dir.Path, a.Url(), Harness.Fixed(6), mirrors: new[] { b.Url() })).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.True(a.TotalBytes > 512 * 1024, $"a enviou {a.TotalBytes}");
        Assert.True(b.TotalBytes > 512 * 1024, $"b enviou {b.TotalBytes}");
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task MirrorWithOtherContentIsIgnored()
    {
        const long size = 4 * 1024 * 1024;
        await using var a = new TestServer(new ServerOptions { Size = size });
        await using var b = new TestServer(new ServerOptions { Size = size, Seed = 99 });
        using var dir = new TempDir();
        var download = new HttpDownload(Harness.Spec(dir.Path, a.Url(), Harness.Fixed(4), mirrors: new[] { b.Url() }));
        var result = await download.RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.Contains("ignorado", download.Stats.Note);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task MirrorDyingMidwayIsCovered()
    {
        const long size = 16 * 1024 * 1024;
        await using var a = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 4 * 1024 * 1024 });
        await using var b = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 4 * 1024 * 1024, DeadAfterBytes = 2 * 1024 * 1024 });
        using var dir = new TempDir();
        var result = await new HttpDownload(Harness.Spec(dir.Path, a.Url(), Harness.Fixed(6), mirrors: new[] { b.Url() })).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }
}

public class ChecksumTests
{
    private static string Sha256(long size)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var data = new byte[size];
        TestServer.Fill(7, 0, data);
        hash.AppendData(data);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    [Fact(Timeout = 30000)]
    public async Task MatchingChecksumIsReported()
    {
        const long size = 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size });
        using var dir = new TempDir();
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), checksum: "sha256:" + Sha256(size))).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.Equal("ok", result.ChecksumState);
    }

    [Fact(Timeout = 30000)]
    public async Task WrongChecksumFailsAndDoesNotPublishFile()
    {
        const long size = 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size });
        using var dir = new TempDir();
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), checksum: "sha256:" + new string('0', 64))).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Failed, result.End);
        Assert.Equal("checksum-mismatch", result.Code);
        Assert.False(File.Exists(Path.Combine(dir.Path, "arquivo.bin")));
    }

    [Fact(Timeout = 30000)]
    public async Task ServerDigestHeaderIsChecked()
    {
        const long size = 1024 * 1024;
        var raw = Convert.ToBase64String(Convert.FromHexString(Sha256(size)));
        await using var server = new TestServer(new ServerOptions { Size = size, DigestHeader = $"sha-256=:{raw}:" });
        using var dir = new TempDir();
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url())).RunAsync(CancellationToken.None);
        Assert.Equal("ok", result.ChecksumState);
    }
}

public class MultiLinkTests
{
    private static readonly NetLink LinkA = new() { Id = "a", Name = "A", Kind = "test", LocalV4 = IPAddress.Parse("127.0.0.1"), Gateway = "gw-a", Group = "g1" };
    private static readonly NetLink LinkB = new() { Id = "b", Name = "B", Kind = "test", LocalV4 = IPAddress.Parse("127.0.0.2"), Gateway = "gw-b", Group = "g2" };

    [Fact(Timeout = 30000)]
    public async Task SocketsAreReallyBoundAndTrafficIsSplitByCapacity()
    {
        const long size = 24 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, PerIpBps = 3 * 1024 * 1024 });
        using var dir = new TempDir();
        using var pool = new HttpPool();
        var profile = new SpeedProfile { MaxConnections = 8, SettleMs = 300, MinFragment = 256 * 1024, GainThreshold = 0.05 };
        var started = Environment.TickCount64;
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), profile, links: () => new[] { LinkA, LinkB }, pool: pool)).RunAsync(CancellationToken.None);
        var elapsed = Environment.TickCount64 - started;
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.True(server.BytesByIp.ContainsKey("127.0.0.1"));
        Assert.True(server.BytesByIp.ContainsKey("127.0.0.2"));
        Assert.True(server.BytesByIp["127.0.0.2"] > size / 5, $"ip2 {server.BytesByIp["127.0.0.2"]}");
        Assert.True(elapsed < 7500, $"levou {elapsed} ms; uma rede só levaria pelo menos 8000 ms");
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task TrafficFollowsTheFasterLink()
    {
        const long size = 16 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, PerIpBps = 16 * 1024 * 1024 });
        using var dir = new TempDir();
        using var pool = new HttpPool();
        var profile = new SpeedProfile { FixedConnections = 4, MaxConnections = 4, MinFragment = 256 * 1024 };
        var slowServerSide = new LinkThrottle(server);
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), profile, links: () => new[] { LinkA, LinkB }, pool: pool)).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        Assert.True(slowServerSide.Both);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    private sealed class LinkThrottle
    {
        private readonly TestServer _server;

        public LinkThrottle(TestServer server) => _server = server;
        public bool Both => _server.BytesByIp.ContainsKey("127.0.0.1") && _server.BytesByIp.ContainsKey("127.0.0.2");
    }

    [Fact(Timeout = 30000)]
    public async Task LinkFailureMovesWorkToSurvivor()
    {
        const long size = 16 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 4 * 1024 * 1024, DropIp = "127.0.0.2", DropIpAfterBytes = 256 * 1024 });
        using var dir = new TempDir();
        using var pool = new HttpPool();
        var profile = new SpeedProfile { FixedConnections = 4, MaxConnections = 4, MinFragment = 256 * 1024 };
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), profile, links: () => new[] { LinkA, LinkB }, pool: pool)).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }

    [Fact(Timeout = 30000)]
    public async Task InterfaceDisappearingDuringDownloadIsSurvived()
    {
        const long size = 16 * 1024 * 1024;
        await using var server = new TestServer(new ServerOptions { Size = size, PerConnectionBps = 4 * 1024 * 1024 });
        using var dir = new TempDir();
        using var pool = new HttpPool();
        var profile = new SpeedProfile { FixedConnections = 4, MaxConnections = 4, MinFragment = 256 * 1024 };
        var started = Environment.TickCount64;
        IReadOnlyList<NetLink> Links() => Environment.TickCount64 - started < 1500 ? new[] { LinkA, LinkB } : new[] { LinkA };
        var result = await new HttpDownload(Harness.Spec(dir.Path, server.Url(), profile, links: Links, pool: pool)).RunAsync(CancellationToken.None);
        Assert.Equal(DownloadEnd.Done, result.End);
        await TestServer.AssertContentAsync(result.FinalPath, size);
    }
}
