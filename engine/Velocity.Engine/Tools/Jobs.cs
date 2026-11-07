using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Velocity.Engine.Core;
using Velocity.Engine.Persist;

namespace Velocity.Engine.Tools;

public interface IJob
{
    DownloadStats Stats { get; }
    MapSnapshot? Snapshot();
    Task<DownloadResult> RunAsync(CancellationToken ct);
}

public sealed class HttpJob : IJob
{
    private readonly HttpDownload _download;

    public HttpJob(HttpDownload download) => _download = download;
    public DownloadStats Stats => _download.Stats;
    public MapSnapshot? Snapshot() => _download.Snapshot();
    public Task<DownloadResult> RunAsync(CancellationToken ct) => _download.RunAsync(ct);
}

public static class ToolFinder
{
    public static string? ToolsDir { get; set; }

    public static string? Find(string name)
    {
        var file = name + ".exe";
        if (!string.IsNullOrEmpty(ToolsDir))
        {
            var candidate = Path.Combine(ToolsDir, file);
            if (File.Exists(candidate)) return candidate;
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var part in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (!Path.IsPathFullyQualified(part)) continue;
                var candidate = Path.Combine(part, file);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    public static string? Version(string exe, string argument)
    {
        try
        {
            var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process == null) return null;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var reading = process.StandardOutput.ReadLineAsync();
            if (!reading.Wait(10000))
            {
                process.Kill(true);
                return null;
            }
            if (!process.WaitForExit(3000)) process.Kill(true);
            return reading.Result;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

public sealed class Aria2Daemon : IDisposable
{
    private readonly object _gate = new();
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private Process? _process;
    private int _port;
    private string _secret = "";
    private int _nextId;

    public static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            var exe = ToolFinder.Find("aria2c") ?? throw new FatalToolException("tool-missing", "O aria2 não está disponível. Torrent e magnet precisam dele.");
            _port = FreePort();
            _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[]
            {
                "--enable-rpc=true", "--rpc-listen-all=false", $"--rpc-listen-port={_port}", $"--rpc-secret={_secret}",
                "--no-conf=true", "--check-certificate=true", "--seed-time=0", "--bt-save-metadata=false", "--bt-metadata-only=false",
                "--follow-torrent=mem", "--continue=true", "--max-concurrent-downloads=16", "--file-allocation=none", "--console-log-level=error", "--summary-interval=0",
            }) info.ArgumentList.Add(arg);
            _process = Process.Start(info) ?? throw new FatalToolException("tool-failed", "Não foi possível iniciar o aria2.");
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        for (var i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await CallRawAsync("aria2.getVersion", new JsonArray(), ct);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(100, ct);
            }
        }
        throw new FatalToolException("tool-failed", "O aria2 não respondeu.");
    }

    private async Task<JsonNode?> CallRawAsync(string method, JsonArray parameters, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture),
            ["method"] = method,
        };
        var all = new JsonArray { (JsonNode)$"token:{_secret}" };
        foreach (var item in parameters.ToArray())
        {
            parameters.Remove(item);
            all.Add(item);
        }
        body["params"] = all;
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.PostAsync($"http://127.0.0.1:{_port}/jsonrpc", content, ct);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        if (node?["error"] is JsonObject error) throw new FatalToolException("aria2", Json.Str(error, "message", "O aria2 recusou o pedido."));
        return node?["result"];
    }

    public async Task<JsonNode?> CallAsync(string method, JsonArray parameters, CancellationToken ct)
    {
        await EnsureAsync(ct);
        return await CallRawAsync(method, parameters, ct);
    }

    public void Dispose()
    {
        try
        {
            lock (_gate)
            {
                if (_process is { HasExited: false }) _process.Kill(true);
                _process?.Dispose();
                _process = null;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        _http.Dispose();
    }
}

public sealed class FatalToolException : Exception
{
    public string Code { get; }

    public FatalToolException(string code, string message) : base(message) => Code = code;
}

public sealed class Aria2Job : IJob
{
    private readonly Aria2Daemon _daemon;
    private readonly string _uri;
    private readonly string _folder;
    private DownloadStats _stats = new() { Resumable = true };

    public Aria2Job(Aria2Daemon daemon, string uri, string folder)
    {
        _daemon = daemon;
        _uri = uri;
        _folder = folder;
    }

    public DownloadStats Stats => Volatile.Read(ref _stats);
    public MapSnapshot? Snapshot() => null;
    public string? Name { get; private set; }

    public async Task<DownloadResult> RunAsync(CancellationToken ct)
    {
        string? gid = null;
        try
        {
            var options = new JsonObject { ["dir"] = _folder };
            if (_uri.StartsWith("file:", StringComparison.Ordinal))
            {
                var path = _uri[5..];
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 10 * 1024 * 1024) return new DownloadResult(DownloadEnd.Failed, "torrent-file", "O arquivo .torrent não foi encontrado ou é grande demais.");
                var base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct));
                gid = (await _daemon.CallAsync("aria2.addTorrent", new JsonArray { (JsonNode)base64, new JsonArray(), options }, ct))?.GetValue<string>();
            }
            else
            {
                gid = (await _daemon.CallAsync("aria2.addUri", new JsonArray { new JsonArray { (JsonNode)_uri }, options }, ct))?.GetValue<string>();
            }
            if (gid == null) return new DownloadResult(DownloadEnd.Failed, "aria2", "O aria2 não aceitou o link.");
            double max = 0;
            var started = Environment.TickCount64;
            while (true)
            {
                await Task.Delay(1000, ct);
                var status = await _daemon.CallAsync("aria2.tellStatus", new JsonArray { (JsonNode)gid }, ct) as JsonObject;
                if (status == null) continue;
                if (status["followedBy"] is JsonArray { Count: > 0 } followed && Json.Str(status, "status") == "complete")
                {
                    gid = followed[0]!.GetValue<string>();
                    continue;
                }
                var total = ParseLong(status, "totalLength");
                var done = ParseLong(status, "completedLength");
                var speed = ParseLong(status, "downloadSpeed");
                var connections = (int)ParseLong(status, "connections");
                max = Math.Max(max, speed);
                Name = NameOf(status);
                var state = Json.Str(status, "status");
                var elapsed = Math.Max(1, Environment.TickCount64 - started) / 1000.0;
                Volatile.Write(ref _stats, new DownloadStats
                {
                    Phase = total == 0 ? "metadata" : "downloading",
                    Done = done,
                    Size = total > 0 ? total : -1,
                    Speed = speed,
                    Max = max,
                    Average = done / elapsed,
                    EtaSeconds = speed > 0 && total > 0 ? (total - done) / (double)speed : -1,
                    Connections = connections,
                    Target = connections,
                    Fragments = (int)ParseLong(status, "numPieces"),
                    Note = Name ?? "",
                });
                if (state == "complete")
                {
                    await RemoveResultAsync(gid);
                    var final = Name != null ? Path.Combine(_folder, Name) : _folder;
                    return new DownloadResult(DownloadEnd.Done, FinalPath: final, Size: total, AverageBps: done / elapsed, MaxBps: max, BestConnections: Math.Max(1, connections));
                }
                if (state is "error" or "removed")
                {
                    var message = Json.Str(status, "errorMessage", "O aria2 não conseguiu concluir o download.");
                    await RemoveResultAsync(gid);
                    return new DownloadResult(DownloadEnd.Failed, "aria2", message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (gid != null) await StopAsync(gid);
            return new DownloadResult(DownloadEnd.Paused);
        }
        catch (FatalToolException e)
        {
            return new DownloadResult(DownloadEnd.Failed, e.Code, e.Message);
        }
        catch (HttpRequestException)
        {
            return new DownloadResult(DownloadEnd.Failed, "aria2", "A comunicação com o aria2 falhou.");
        }
    }

    private async Task StopAsync(string gid)
    {
        try
        {
            using var timeout = new CancellationTokenSource(3000);
            await _daemon.CallAsync("aria2.forceRemove", new JsonArray { (JsonNode)gid }, timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or FatalToolException)
        {
        }
    }

    private async Task RemoveResultAsync(string gid)
    {
        try
        {
            using var timeout = new CancellationTokenSource(3000);
            await _daemon.CallAsync("aria2.removeDownloadResult", new JsonArray { (JsonNode)gid }, timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or FatalToolException)
        {
        }
    }

    private static string? NameOf(JsonObject status)
    {
        var name = status["bittorrent"]?["info"]?["name"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(name)) return Velocity.Engine.Net.FileNames.Sanitize(name);
        var first = status["files"]?[0]?["path"]?.GetValue<string>();
        return string.IsNullOrEmpty(first) || first.StartsWith("[METADATA]", StringComparison.Ordinal) ? null : Path.GetFileName(first);
    }

    private static long ParseLong(JsonObject status, string key) =>
        long.TryParse(Json.Str(status, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

public sealed class YtDlpJob : IJob
{
    private readonly string _url;
    private readonly string _folder;
    private readonly string _mode;
    private DownloadStats _stats = new() { Resumable = true };

    public YtDlpJob(string url, string folder, string mode)
    {
        _url = url;
        _folder = folder;
        _mode = mode;
    }

    public DownloadStats Stats => Volatile.Read(ref _stats);
    public MapSnapshot? Snapshot() => null;
    public string? Name { get; private set; }

    public static List<string> BuildArguments(string url, string folder, string mode, string? ffmpeg)
    {
        var args = new List<string>
        {
            "--ignore-config", "--no-exec", "--no-playlist", "--newline", "--no-colors", "--windows-filenames", "--continue", "--no-simulate", "--no-quiet",
            "--concurrent-fragments", mode switch { "turbo" => "8", "eco" => "1", _ => "4" },
            "-P", folder,
            "-o", "%(title).150B [%(id).40B].%(ext)s",
            "--progress-template", "download:VP|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s",
            "--print", "after_move:VF|%(filepath)s",
        };
        if (ffmpeg != null)
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpeg);
        }
        else
        {
            args.Add("-f");
            args.Add("b");
        }
        args.Add("--");
        args.Add(url);
        return args;
    }

    public async Task<DownloadResult> RunAsync(CancellationToken ct)
    {
        var exe = ToolFinder.Find("yt-dlp");
        if (exe == null) return new DownloadResult(DownloadEnd.Failed, "tool-missing", "O yt-dlp não está disponível. Vídeos de páginas precisam dele.");
        Directory.CreateDirectory(_folder);
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _folder,
        };
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        var ffmpeg = ToolFinder.Find("ffmpeg");
        foreach (var arg in BuildArguments(_url, _folder, _mode, ffmpeg)) info.ArgumentList.Add(arg);
        using var process = Process.Start(info);
        if (process == null) return new DownloadResult(DownloadEnd.Failed, "tool-failed", "Não foi possível iniciar o yt-dlp.");
        string? final = null;
        var lastError = "";
        double max = 0;
        long done = 0, total = -1;
        var started = Environment.TickCount64;
        var errors = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
                if (line.StartsWith("ERROR:", StringComparison.Ordinal)) lastError = line[6..].Trim();
        }, CancellationToken.None);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("VF|", StringComparison.Ordinal))
                {
                    final = line[3..].Trim();
                    Name = Path.GetFileName(final);
                }
                else if (line.StartsWith("VP|", StringComparison.Ordinal))
                {
                    var parts = line.Split('|');
                    if (parts.Length < 6) continue;
                    done = Number(parts[1]) is { } d ? (long)d : done;
                    total = Number(parts[2]) is { } t ? (long)t : Number(parts[3]) is { } e ? (long)e : total;
                    var speed = Number(parts[4]) ?? 0;
                    max = Math.Max(max, speed);
                    var elapsed = Math.Max(1, Environment.TickCount64 - started) / 1000.0;
                    Volatile.Write(ref _stats, new DownloadStats
                    {
                        Phase = "downloading",
                        Done = done,
                        Size = total,
                        Speed = speed,
                        Max = max,
                        Average = done / elapsed,
                        EtaSeconds = Number(parts[5]) ?? -1,
                        Connections = 1,
                        Target = 1,
                        Fragments = 1,
                    });
                }
            }
            await process.WaitForExitAsync(ct);
            await errors;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Kill(process);
            return new DownloadResult(DownloadEnd.Paused);
        }
        if (process.ExitCode != 0 || final == null)
            return new DownloadResult(DownloadEnd.Failed, "yt-dlp", lastError.Length > 0 ? Limit(lastError) : "O yt-dlp não conseguiu baixar esse vídeo.");
        var size = File.Exists(final) ? new FileInfo(final).Length : done;
        var seconds = Math.Max(1, Environment.TickCount64 - started) / 1000.0;
        NativeDisk.MarkFromInternet(final);
        return new DownloadResult(DownloadEnd.Done, FinalPath: final, Size: size, AverageBps: size / seconds, MaxBps: max, BestConnections: 1);
    }

    private static string Limit(string text) => text.Length > 300 ? text[..300] : text;

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
