using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Velocity.Engine.Tests;

public sealed class ServerOptions
{
    public long Size = 8 * 1024 * 1024;
    public int Seed = 7;
    public bool Ranges = true;
    public bool IgnoreRangeOnWorkers;
    public bool BadContentRangeOnWorkers;
    public string Etag = "\"abc\"";
    public string LastModified = "Sat, 01 Jan 2022 00:00:00 GMT";
    public long PerConnectionBps;
    public long PerIpBps;
    public int MaxConcurrent;
    public int OverLimitStatus = 503;
    public int RetryAfterSeconds;
    public int DropAfterBytes;
    public int DropTimes;
    public string? SwitchEtagAfterBytes;
    public long SwitchEtagAt;
    public string? RequireBasic;
    public string FileName = "arquivo.bin";
    public string ContentType = "application/octet-stream";
    public bool Chunked;
    public int ExpireTokenAfterBytes;
    public int HangAfterBytes;
    public int HangTimes;
    public HashSet<string> DenyIps = new();
    public int DropIpAfterBytes;
    public string? DropIp;
    public string? DigestHeader;
    public int DeadAfterBytes;
}

public sealed class TestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private int _active;
    private int _maxActive;
    private long _bytes;
    private int _drops;
    private int _hangs;
    private int _rejected;
    private int _tokenGeneration;
    private long _tokenBytes;
    public readonly ServerOptions Options;
    public readonly ConcurrentDictionary<string, long> BytesByIp = new();
    public readonly ConcurrentQueue<string> Authorizations = new();
    public readonly ConcurrentQueue<string> Paths = new();
    private readonly ConcurrentDictionary<string, IpBucket> _buckets = new();
    private volatile bool _dead;

    private sealed class IpBucket
    {
        public readonly object Gate = new();
        public double Tokens;
        public long Last = Environment.TickCount64;
    }

    public TestServer(ServerOptions options)
    {
        Options = options;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(512);
        _accept = Task.Run(AcceptLoop);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Uri Url(string path = "/arquivo.bin") => new($"http://127.0.0.1:{Port}{path}");
    public int MaxActive => Volatile.Read(ref _maxActive);
    public long TotalBytes => Interlocked.Read(ref _bytes);
    public int Rejected => Volatile.Read(ref _rejected);

    public static void Fill(int seed, long offset, Span<byte> target)
    {
        for (var i = 0; i < target.Length; i++)
        {
            var position = offset + i;
            var word = (ulong)(position / 8) * 0x9E3779B97F4A7C15UL ^ (ulong)seed;
            target[i] = (byte)(word >> (int)(position % 8 * 8));
        }
    }

    public static async Task AssertContentAsync(string path, long size, int seed = 7)
    {
        var info = new FileInfo(path);
        Xunit.Assert.Equal(size, info.Length);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        var expected = new byte[1024 * 1024];
        var offset = 0L;
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            Fill(seed, offset, expected.AsSpan(0, read));
            if (!buffer.AsSpan(0, read).SequenceEqual(expected.AsSpan(0, read))) throw new Xunit.Sdk.XunitException($"Conteúdo divergente perto do byte {offset}");
            offset += read;
        }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => Serve(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
            var stream = client.GetStream();
            var reader = new byte[8192];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var head = await ReadHeadAsync(stream, reader);
                    if (head == null) return;
                    var keep = await Respond(stream, head, ip);
                    if (!keep) return;
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task<string?> ReadHeadAsync(NetworkStream stream, byte[] buffer)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), _stop.Token);
            if (read == 0) return null;
            builder.Append((char)buffer[0]);
            if (builder.Length >= 4 && builder[^1] == '\n' && builder[^2] == '\r' && builder[^3] == '\n' && builder[^4] == '\r') return builder.ToString();
            if (builder.Length > 16384) return null;
        }
    }

    private static string Text(int status, string extra = "", string body = "") =>
        $"HTTP/1.1 {status} X\r\n{extra}Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: keep-alive\r\n\r\n{body}";

    private async Task<bool> SendTextAsync(NetworkStream stream, string response)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(response), _stop.Token);
        return true;
    }

    private async Task<bool> Respond(NetworkStream stream, string head, string ip)
    {
        var lines = head.Split("\r\n");
        var path = lines[0].Split(' ')[1];
        Paths.Enqueue(path);
        var headers = lines.Skip(1).Where(l => l.Contains(':')).ToDictionary(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant(), l => l[(l.IndexOf(':') + 1)..].Trim());
        if (headers.TryGetValue("authorization", out var auth)) Authorizations.Enqueue($"{path} {auth}");
        var plainPath = path.Split('?')[0];
        if (Options.DenyIps.Contains(ip)) return false;
        if (plainPath == "/redir") return await SendTextAsync(stream, Text(302, "Location: /arquivo.bin\r\n"));
        if (plainPath == "/loop") return await SendTextAsync(stream, Text(302, "Location: /loop\r\n"));
        if (plainPath == "/ftp") return await SendTextAsync(stream, Text(302, "Location: ftp://exemplo/x\r\n"));
        if (plainPath == "/cross" && headers.TryGetValue("x-target", out _)) return true;
        if (plainPath.StartsWith("/go/", StringComparison.Ordinal)) return await SendTextAsync(stream, Text(302, $"Location: http://127.0.0.1:{plainPath[4..]}/arquivo.bin\r\n"));
        if (plainPath == "/link")
        {
            var generation = Volatile.Read(ref _tokenGeneration);
            return await SendTextAsync(stream, Text(302, $"Location: /tok?t={generation}\r\n"));
        }
        if (plainPath == "/html") return await SendTextAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 5\r\nConnection: keep-alive\r\n\r\n<a/>\n");
        if (plainPath == "/missing") return await SendTextAsync(stream, Text(404));
        if (_dead) return false;
        if (Options.RequireBasic != null)
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Options.RequireBasic));
            if (!headers.TryGetValue("authorization", out var given) || given != expected)
                return await SendTextAsync(stream, Text(401, "WWW-Authenticate: Basic realm=\"x\"\r\n"));
        }
        if (plainPath == "/tok")
        {
            var token = path.Contains("t=") ? path[(path.IndexOf("t=", StringComparison.Ordinal) + 2)..] : "";
            if (token != Volatile.Read(ref _tokenGeneration).ToString()) return await SendTextAsync(stream, Text(403));
        }
        long start = 0, end = Options.Size - 1;
        var partial = false;
        var worker = false;
        if (Options.Ranges && headers.TryGetValue("range", out var range) && range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            var parts = range[6..].Split('-');
            start = long.Parse(parts[0]);
            if (parts[1].Length > 0) end = Math.Min(long.Parse(parts[1]), Options.Size - 1);
            partial = true;
            worker = !(start == 0 && end == 0);
            if (start >= Options.Size) return await SendTextAsync(stream, Text(416, $"Content-Range: bytes */{Options.Size}\r\n"));
        }
        var currentEtag = Options.Etag;
        if (Options.SwitchEtagAfterBytes != null && TotalBytes >= Options.SwitchEtagAt) currentEtag = Options.SwitchEtagAfterBytes;
        if (partial && worker && headers.TryGetValue("if-range", out var ifRange) && ifRange != currentEtag && ifRange != Options.LastModified)
        {
            partial = false;
            start = 0;
            end = Options.Size - 1;
        }
        if (worker && Options.IgnoreRangeOnWorkers)
        {
            partial = false;
            start = 0;
            end = Options.Size - 1;
        }
        var over = false;
        var active = Interlocked.Increment(ref _active);
        try
        {
            if (Options.MaxConcurrent > 0 && active > Options.MaxConcurrent && worker)
            {
                over = true;
                Interlocked.Increment(ref _rejected);
                var extra = Options.RetryAfterSeconds > 0 ? $"Retry-After: {Options.RetryAfterSeconds}\r\n" : "";
                return await SendTextAsync(stream, Text(Options.OverLimitStatus, extra));
            }
            UpdateMax(active);
            var builder = new StringBuilder();
            builder.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            builder.Append($"Content-Type: {Options.ContentType}\r\n");
            if (Options.Ranges) builder.Append("Accept-Ranges: bytes\r\n");
            if (currentEtag.Length > 0) builder.Append($"ETag: {currentEtag}\r\n");
            if (Options.LastModified.Length > 0) builder.Append($"Last-Modified: {Options.LastModified}\r\n");
            if (Options.FileName.Length > 0) builder.Append($"Content-Disposition: attachment; filename=\"{Options.FileName}\"\r\n");
            if (Options.DigestHeader != null) builder.Append($"Repr-Digest: {Options.DigestHeader}\r\n");
            var length = end - start + 1;
            if (partial)
            {
                var reportStart = worker && Options.BadContentRangeOnWorkers ? start + 1024 : start;
                builder.Append($"Content-Range: bytes {reportStart}-{end}/{Options.Size}\r\n");
            }
            if (Options.Chunked) builder.Append("Transfer-Encoding: chunked\r\n");
            else builder.Append($"Content-Length: {length}\r\n");
            builder.Append("Connection: keep-alive\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), _stop.Token);
            return await SendBody(stream, ip, start, length, worker);
        }
        finally
        {
            if (!over) { }
            Interlocked.Decrement(ref _active);
        }
    }

    private void UpdateMax(int active)
    {
        int current;
        do
        {
            current = Volatile.Read(ref _maxActive);
            if (active <= current) return;
        } while (Interlocked.CompareExchange(ref _maxActive, active, current) != current);
    }

    public void RotateToken() => Interlocked.Increment(ref _tokenGeneration);
    public void Kill() => _dead = true;

    private async Task<bool> SendBody(NetworkStream stream, string ip, long start, long length, bool worker)
    {
        var chunk = new byte[64 * 1024];
        var sent = 0L;
        var startedAt = Environment.TickCount64;
        while (sent < length)
        {
            var count = (int)Math.Min(chunk.Length, length - sent);
            Fill(Options.Seed, start + sent, chunk.AsSpan(0, count));
            if (Options.Chunked) await stream.WriteAsync(Encoding.ASCII.GetBytes($"{count:X}\r\n"), _stop.Token);
            await stream.WriteAsync(chunk.AsMemory(0, count), _stop.Token);
            if (Options.Chunked) await stream.WriteAsync("\r\n"u8.ToArray(), _stop.Token);
            sent += count;
            Interlocked.Add(ref _bytes, count);
            BytesByIp.AddOrUpdate(ip, count, (_, value) => value + count);
            if (Options.DropAfterBytes > 0 && sent >= Options.DropAfterBytes && Interlocked.Increment(ref _drops) <= Options.DropTimes) return false;
            if (Options.DropIp == ip && sent >= Options.DropIpAfterBytes) return false;
            if (Options.DeadAfterBytes > 0 && TotalBytes >= Options.DeadAfterBytes)
            {
                _dead = true;
                return false;
            }
            if (Options.ExpireTokenAfterBytes > 0 && Interlocked.Add(ref _tokenBytes, count) >= Options.ExpireTokenAfterBytes)
            {
                Interlocked.Exchange(ref _tokenBytes, 0);
                RotateToken();
                return false;
            }
            if (Options.HangAfterBytes > 0 && sent >= Options.HangAfterBytes && Interlocked.Increment(ref _hangs) <= Options.HangTimes)
            {
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }
            if (Options.PerConnectionBps > 0)
            {
                var due = sent * 1000 / Options.PerConnectionBps;
                var elapsed = Environment.TickCount64 - startedAt;
                if (due > elapsed) await Task.Delay((int)(due - elapsed), _stop.Token);
            }
            if (Options.PerIpBps > 0) await TakeIp(ip, count);
        }
        if (Options.Chunked)
        {
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), _stop.Token);
        }
        return true;
    }

    private async Task TakeIp(string ip, int count)
    {
        var bucket = _buckets.GetOrAdd(ip, _ => new IpBucket());
        double wait;
        lock (bucket.Gate)
        {
            var now = Environment.TickCount64;
            bucket.Tokens = Math.Min(bucket.Tokens + (now - bucket.Last) * Options.PerIpBps / 1000.0, Options.PerIpBps / 10.0);
            bucket.Last = now;
            bucket.Tokens -= count;
            wait = bucket.Tokens >= 0 ? 0 : -bucket.Tokens * 1000.0 / Options.PerIpBps;
        }
        if (wait > 0) await Task.Delay((int)wait, _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _accept;
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException)
        {
        }
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "velocity-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
