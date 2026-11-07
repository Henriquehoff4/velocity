using System.Net;
using Velocity.Engine.Core;
using Velocity.Engine.Net;
using Xunit;

namespace Velocity.Engine.Tests;

public class RangeListTests
{
    [Fact(Timeout = 30000)]
    public async Task MergesAndRemoves()
    {
        await Task.Yield();
        var list = new RangeList();
        list.Add(10, 20);
        list.Add(30, 40);
        list.Add(20, 30);
        Assert.Single(list.Items);
        Assert.Equal(30, list.Total);
        list.Remove(15, 25);
        Assert.Equal(new[] { (10L, 15L), (25L, 40L) }, list.Items);
        Assert.Equal(20, list.Total);
        list.Add(0, 100);
        Assert.Single(list.Items);
        Assert.Equal(100, list.Total);
    }

    [Fact(Timeout = 30000)]
    public async Task RandomOperationsMatchModel()
    {
        await Task.Yield();
        var random = new Random(42);
        var list = new RangeList();
        var model = new bool[2000];
        for (var i = 0; i < 3000; i++)
        {
            var a = random.Next(2000);
            var b = Math.Min(2000, a + random.Next(1, 100));
            var add = random.Next(3) != 0;
            if (add) list.Add(a, b);
            else list.Remove(a, b);
            for (var x = a; x < b; x++) model[x] = add;
        }
        var fromList = new bool[2000];
        foreach (var (s, e) in list.Items)
            for (var x = s; x < e; x++) fromList[x] = true;
        Assert.Equal(model, fromList);
        Assert.Equal(model.Count(v => v), list.Total);
    }
}

public class SegmentMapTests
{
    [Fact(Timeout = 30000)]
    public async Task StealingCoversFileExactlyOnce()
    {
        await Task.Yield();
        const long size = 64 * 1024 * 1024;
        var map = new SegmentMap(size);
        var random = new Random(3);
        var coverage = new RangeList();
        var written = 0L;
        var active = new List<Segment>();
        var worker = 0;
        while (!map.IsComplete)
        {
            if (active.Count < 8)
            {
                var seg = map.Acquire(256 * 1024, "l", ++worker);
                if (seg != null) active.Add(seg);
            }
            if (active.Count == 0) continue;
            var pick = active[random.Next(active.Count)];
            var allowed = map.Reserve(pick, random.Next(1, 1024 * 1024));
            if (allowed > 0)
            {
                coverage.Add(pick.Pos, pick.Pos + allowed);
                written += allowed;
                map.Commit(pick, allowed);
            }
            if (pick.Pos >= map.EndOf(pick) || random.Next(50) == 0)
            {
                map.Release(pick);
                active.Remove(pick);
            }
        }
        Assert.Equal(size, written);
        Assert.Equal(size, coverage.Total);
        Assert.Single(coverage.Items);
    }

    [Fact(Timeout = 30000)]
    public async Task StealNeverTakesReservedBytes()
    {
        await Task.Yield();
        var map = new SegmentMap(4 * 1024 * 1024);
        var first = map.Acquire(256 * 1024, "a", 1)!;
        Assert.Equal(1024 * 1024, map.Reserve(first, 1024 * 1024));
        var second = map.Acquire(256 * 1024, "b", 2)!;
        Assert.True(second.Start >= first.Reserved);
        Assert.Equal(second.Start, map.EndOf(first));
    }

    [Fact(Timeout = 30000)]
    public async Task ResumeSkipsDoneRanges()
    {
        await Task.Yield();
        var map = new SegmentMap(100, new[] { (0L, 40L), (60L, 100L) });
        Assert.Equal(80, map.DoneBytes);
        var seg = map.Acquire(1, "a", 1)!;
        Assert.Equal(40, seg.Start);
        Assert.Equal(60, seg.End);
    }

    [Fact(Timeout = 30000)]
    public async Task ReleaseReturnsRemainderToPool()
    {
        await Task.Yield();
        var map = new SegmentMap(1000);
        var seg = map.Acquire(1, "a", 1)!;
        map.Commit(seg, map.Reserve(seg, 100));
        map.Release(seg);
        var next = map.Acquire(1, "b", 2)!;
        Assert.Equal(100, next.Start);
        Assert.Equal(1000, next.End);
    }
}

public class JournalTests
{
    private static readonly JournalHeader Header = new(1000, "\"e\"", "lm");

    [Fact(Timeout = 30000)]
    public async Task TornTailIsDiscarded()
    {
        await Task.Yield();
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "j.vdl");
        using (var journal = Journal.Create(path, Header))
            journal.Append(new[] { new Extent(0, 100, 1), new Extent(100, 100, 2), new Extent(200, 50, 3) });
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^10]);
        var opened = Journal.Open(path, Header);
        Assert.NotNull(opened);
        Assert.Equal(2, opened!.Value.Extents.Count);
        opened.Value.Journal.Append(new[] { new Extent(200, 50, 9) });
        opened.Value.Journal.Dispose();
        var again = Journal.Open(path, Header)!;
        Assert.Equal(3, again.Value.Extents.Count);
        Assert.Equal(9UL, again.Value.Extents[2].Hash);
        again.Value.Journal.Dispose();
    }

    [Fact(Timeout = 30000)]
    public async Task CorruptRecordStopsReading()
    {
        await Task.Yield();
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "j.vdl");
        using (var journal = Journal.Create(path, Header))
            journal.Append(new[] { new Extent(0, 100, 1), new Extent(100, 100, 2) });
        var bytes = File.ReadAllBytes(path);
        bytes[^30] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        var opened = Journal.Open(path, Header)!;
        Assert.Empty(opened.Value.Extents);
        opened.Value.Journal.Dispose();
    }

    [Fact(Timeout = 30000)]
    public async Task DifferentFileIsRejected()
    {
        await Task.Yield();
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "j.vdl");
        using (Journal.Create(path, Header)) { }
        Assert.Null(Journal.Open(path, Header with { Etag = "\"outro\"" }));
        Assert.Null(Journal.Open(path, Header with { Size = 999 }));
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        Assert.Null(Journal.Open(path, Header));
    }
}

public class ControllerTests
{
    private sealed class Sim
    {
        public long Now;
        public ConnectionController Controller = null!;

        public void Run(Func<IReadOnlyList<LinkControl>, Dictionary<string, double>> model, int seconds, bool bounded = false)
        {
            for (var i = 0; i < seconds * 5; i++)
            {
                Now += 200;
                var speeds = model(Controller.Links);
                Controller.Tick(Now, speeds.Values.Sum(), speeds, bounded);
            }
        }
    }

    private static ControllerOptions Fast(int max = 32) => new() { Max = max, SettleMs = 400, WindowTicks = 3, HoldMs = 5000, MaxStep = 2 };

    [Fact(Timeout = 30000)]
    public async Task ClimbsWhenServerThrottlesPerConnection()
    {
        await Task.Yield();
        var sim = new Sim { Controller = new ConnectionController(Fast(16), new[] { "a" }) };
        sim.Run(links => new() { ["a"] = links[0].Target * 1e6 }, 40);
        Assert.Equal(16, sim.Controller.TargetFor("a"));
    }

    [Fact(Timeout = 30000)]
    public async Task StopsClimbingWhenLinkSaturates()
    {
        await Task.Yield();
        var sim = new Sim { Controller = new ConnectionController(Fast(), new[] { "a" }) };
        sim.Run(links => new() { ["a"] = Math.Min(links[0].Target, 4) * 1e6 }, 30);
        Assert.InRange(sim.Controller.TargetFor("a"), 4, 5);
        Assert.True(sim.Controller.Saturated);
    }

    [Fact(Timeout = 30000)]
    public async Task BacksOffWhenMoreConnectionsHurt()
    {
        await Task.Yield();
        var sim = new Sim { Controller = new ConnectionController(Fast(), new[] { "a" }) };
        sim.Run(links => new() { ["a"] = links[0].Target <= 3 ? links[0].Target * 1e6 : Math.Max(5e5, 3e6 - (links[0].Target - 3) * 5e5) }, 30);
        Assert.InRange(sim.Controller.TargetFor("a"), 3, 4);
    }

    [Fact(Timeout = 30000)]
    public async Task ThrottleHalvesAndWaits()
    {
        await Task.Yield();
        var sim = new Sim { Controller = new ConnectionController(new ControllerOptions { Max = 32, Start = 16, SettleMs = 400, WindowTicks = 3, HoldMs = 5000 }, new[] { "a" }) };
        sim.Controller.OnThrottle("a", 1000, 10000);
        Assert.Equal(8, sim.Controller.TargetFor("a"));
        Assert.True(sim.Controller.ThrottledUntil >= 11000);
        sim.Now = 1000;
        sim.Run(links => new() { ["a"] = links[0].Target * 1e6 }, 8);
        Assert.Equal(8, sim.Controller.TargetFor("a"));
        sim.Run(links => new() { ["a"] = links[0].Target * 1e6 }, 40);
        Assert.Equal(9, sim.Controller.TargetFor("a"));
    }

    [Fact(Timeout = 30000)]
    public async Task ConnectionLimitBecomesCap()
    {
        await Task.Yield();
        var sim = new Sim { Controller = new ConnectionController(new ControllerOptions { Max = 16, Start = 5, SettleMs = 400, WindowTicks = 3, HoldMs = 1000 }, new[] { "a" }) };
        sim.Controller.OnConnectionLimit("a", 0);
        sim.Run(links => new() { ["a"] = links[0].Target * 1e6 }, 30);
        Assert.Equal(4, sim.Controller.TargetFor("a"));
    }

    [Fact(Timeout = 30000)]
    public async Task IndependentLinksBothGetConnections()
    {
        await Task.Yield();
        var sim = new Sim { Controller = new ConnectionController(Fast(), new[] { "a", "b" }) };
        sim.Run(links => new()
        {
            ["a"] = Math.Min(links[0].Target, 4) * 2e6,
            ["b"] = Math.Min(links[1].Target, 2) * 1e6,
        }, 60);
        Assert.True(sim.Controller.TargetFor("a") >= 4);
        Assert.True(sim.Controller.TargetFor("b") >= 2);
    }

    [Fact(Timeout = 30000)]
    public async Task SharedUplinkIsDroppedAfterMeasuringNoGain()
    {
        await Task.Yield();
        var sim = new Sim { Controller = new ConnectionController(Fast(), new[] { "a", "b" }) };
        sim.Run(links =>
        {
            var total = Math.Min(links[0].Target + links[1].Target, 4) * 1e6;
            var sum = Math.Max(1, links[0].Target + links[1].Target);
            return new() { ["a"] = total * links[0].Target / sum, ["b"] = total * links[1].Target / sum };
        }, 12);
        var b = sim.Controller.Links.First(l => l.Id == "b");
        Assert.Equal(0, b.Target);
        Assert.True(b.NoGain);
    }

    [Fact(Timeout = 30000)]
    public async Task LinkDownMovesWorkToSurvivor()
    {
        await Task.Yield();
        var sim = new Sim { Controller = new ConnectionController(Fast(), new[] { "a", "b" }) };
        sim.Controller.SetLinkDown("a", true, 0);
        Assert.Equal(0, sim.Controller.TargetFor("a"));
        Assert.Equal(1, sim.Controller.TargetFor("b"));
    }

    [Fact(Timeout = 30000)]
    public async Task BoundedByDiskOrLimitDoesNotClimb()
    {
        await Task.Yield();
        var sim = new Sim { Controller = new ConnectionController(Fast(), new[] { "a" }) };
        sim.Run(links => new() { ["a"] = links[0].Target * 1e6 }, 20, bounded: true);
        Assert.Equal(1, sim.Controller.TargetFor("a"));
    }

    [Fact(Timeout = 30000)]
    public async Task FixedModeStaysFixedButRespectsThrottle()
    {
        await Task.Yield();
        var sim = new Sim { Controller = new ConnectionController(new ControllerOptions { Max = 8, Fixed = 8 }, new[] { "a" }) };
        sim.Run(links => new() { ["a"] = 1e6 }, 10);
        Assert.Equal(8, sim.Controller.TargetFor("a"));
        sim.Controller.OnThrottle("a", sim.Now, 1000);
        Assert.Equal(4, sim.Controller.TargetFor("a"));
    }
}

public class FileNameTests
{
    [Theory(Timeout = 30000)]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData("..\\..\\Windows\\win.ini", "win.ini")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("a:b.txt", "a_b.txt")]
    [InlineData("arquivo.txt:Zone.Identifier", "arquivo.txt_Zone.Identifier")]
    [InlineData("nome. . ", "nome")]
    [InlineData(".bashrc", "bashrc")]
    [InlineData("foto\u202egnp.exe", "foto_gnp.exe")]
    [InlineData("   ", "")]
    public async Task Sanitizes(string raw, string expected)
    {
        await Task.Yield();
        Assert.Equal(expected, FileNames.Sanitize(raw));
    }

    [Fact(Timeout = 30000)]
    public async Task LimitsLengthKeepingExtension()
    {
        await Task.Yield();
        var name = FileNames.Sanitize(new string('a', 400) + ".zip");
        Assert.True(name.Length <= 180);
        Assert.EndsWith(".zip", name);
    }

    [Fact(Timeout = 30000)]
    public async Task ParsesDigest()
    {
        await Task.Yield();
        Assert.Equal("sha256:" + new string('0', 64), FileNames.ParseDigest("sha-256=:" + Convert.ToBase64String(new byte[32]) + ":"));
        Assert.Equal("", FileNames.ParseDigest("crc32c=abc"));
    }
}

public class NetworkSelectionTests
{
    private static NetLink Link(string id, string gateway, string subnet, long speed = 1000, string kind = "ethernet") =>
        new() { Id = id, Name = id, Kind = kind, LocalV4 = IPAddress.Parse("10.0.0.1"), Gateway = gateway, Subnet = subnet, NominalBps = speed };

    [Fact(Timeout = 30000)]
    public async Task SameRouterIsOneGroupAndPicksFastest()
    {
        await Task.Yield();
        var links = NetworkMonitor.AssignGroups(new() { Link("eth", "192.168.0.1", "192.168.0.0", 125_000_000), Link("wifi", "192.168.0.1", "192.168.0.0", 50_000_000, "wifi") });
        Assert.Equal(links[0].Group, links[1].Group);
        var chosen = NetworkMonitor.SelectForDownload(links, null, false);
        Assert.Single(chosen);
        Assert.Equal("eth", chosen[0].Id);
    }

    [Fact(Timeout = 30000)]
    public async Task DifferentGatewaysAreIndependent()
    {
        await Task.Yield();
        var links = NetworkMonitor.AssignGroups(new() { Link("eth", "192.168.0.1", "192.168.0.0"), Link("phone", "172.20.10.1", "172.20.10.0", 100, "wifi") });
        Assert.NotEqual(links[0].Group, links[1].Group);
        Assert.Equal(2, NetworkMonitor.SelectForDownload(links, null, false).Count);
    }

    [Fact(Timeout = 30000)]
    public async Task ForceAndAllowListAreRespected()
    {
        await Task.Yield();
        var links = NetworkMonitor.AssignGroups(new() { Link("a", "1.1.1.1", "1"), Link("b", "1.1.1.1", "1"), Link("c", "2.2.2.2", "2") });
        Assert.Equal(3, NetworkMonitor.SelectForDownload(links, null, true).Count);
        var only = NetworkMonitor.SelectForDownload(links, new[] { "c" }, false);
        Assert.Equal("c", Assert.Single(only).Id);
    }
}

public class TokenBucketTests
{
    [Fact(Timeout = 30000)]
    public async Task EnforcesRate()
    {
        var bucket = new TokenBucket(2 * 1024 * 1024);
        var started = Environment.TickCount64;
        for (var i = 0; i < 20; i++) await bucket.TakeAsync(100 * 1024, CancellationToken.None);
        var elapsed = Environment.TickCount64 - started;
        Assert.InRange(elapsed, 500, 3000);
    }

    [Fact(Timeout = 30000)]
    public async Task ZeroMeansNoLimit()
    {
        var bucket = new TokenBucket(0);
        var task = bucket.TakeAsync(1 << 30, CancellationToken.None);
        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }
}
