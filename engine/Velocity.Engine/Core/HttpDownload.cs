using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Velocity.Engine.Net;

namespace Velocity.Engine.Core;

public sealed record SourceSpec(Uri Url, RequestAuth? Auth);

public sealed class DownloadSpec
{
    public required string Id { get; init; }
    public required IReadOnlyList<SourceSpec> Sources { get; init; }
    public required string Folder { get; init; }
    public required string PartPath { get; init; }
    public string JournalPath => PartPath + ".vdl";
    public string? FileName { get; init; }
    public string? Checksum { get; init; }
    public long ExpectedSize { get; init; } = -1;
    public string ExpectedEtag { get; init; } = "";
    public string ExpectedModified { get; init; } = "";
    public required SpeedProfile Profile { get; init; }
    public TokenBucket? GlobalLimit { get; init; }
    public required HttpPool Pool { get; init; }
    public Func<IReadOnlyList<NetLink>>? Links { get; init; }
    public HostMemory? Hosts { get; init; }
    public int StallMs { get; init; } = 20000;
    public int NoProgressMs { get; init; } = 300000;
    public bool MarkOfTheWeb { get; init; } = true;
    public Action<ProbeResult>? OnProbed { get; init; }
}

public enum DownloadEnd { Done, Paused, Failed, NeedsLink, NeedsAuth }

public sealed record DownloadResult(DownloadEnd End, string Code = "", string Message = "", string FinalPath = "", long Size = 0, bool Verified = false, string ChecksumState = "", double AverageBps = 0, double MaxBps = 0, int BestConnections = 0);

public sealed record LinkStat(string Id, string Name, double Speed, int Connections, bool NoGain);

public sealed record DownloadStats
{
    public string Phase { get; init; } = "connecting";
    public long Done { get; init; }
    public long Size { get; init; } = -1;
    public double Speed { get; init; }
    public double Average { get; init; }
    public double Max { get; init; }
    public double EtaSeconds { get; init; } = -1;
    public int Connections { get; init; }
    public int Target { get; init; }
    public int Fragments { get; init; }
    public string Bottleneck { get; init; } = "";
    public bool Resumable { get; init; } = true;
    public string Note { get; init; } = "";
    public long VerifyDone { get; init; }
    public IReadOnlyList<LinkStat> Links { get; init; } = Array.Empty<LinkStat>();
}

public sealed class HostMemory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HostProfile> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, HostProfile> All { get { lock (_gate) return new Dictionary<string, HostProfile>(_hosts); } }

    public HostProfile? Get(string host)
    {
        lock (_gate)
        {
            if (!_hosts.TryGetValue(host, out var profile)) return null;
            if (profile.CapUntil > 0 && profile.CapUntil < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return profile with { Cap = 0, CapUntil = 0 };
            return profile;
        }
    }

    public void Set(string host, HostProfile profile)
    {
        lock (_gate) _hosts[host] = profile;
    }

    public void RecordBest(string host, int connections)
    {
        lock (_gate)
        {
            _hosts.TryGetValue(host, out var current);
            _hosts[host] = (current ?? new HostProfile()) with { Best = connections };
        }
    }

    public void RecordCap(string host, int cap)
    {
        lock (_gate)
        {
            _hosts.TryGetValue(host, out var current);
            _hosts[host] = (current ?? new HostProfile()) with { Cap = cap, CapUntil = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds() };
        }
    }
}

public sealed record HostProfile
{
    public int Best { get; init; }
    public int Cap { get; init; }
    public long CapUntil { get; init; }
}

public sealed class HttpDownload
{
    private sealed class SourceState
    {
        public required SourceSpec Spec;
        public required Uri Final;
        public bool Same = true;
        public string Etag = "";
        public long CooldownUntil;
        public int Failures;
        public int Active;
        public long Received;
        public long LastReceived;
        public double Speed;
        public bool Dead;
        public long LastResolve;
        public readonly SemaphoreSlim ResolveGate = new(1, 1);
    }

    private sealed class Worker
    {
        public required int Id;
        public required NetLink Link;
        public required CancellationTokenSource Cts;
        public Task Task = Task.CompletedTask;
        public Segment? Segment;
        public long Received;
        public long LastSample;
        public double Speed;
        public long LastByte = Environment.TickCount64;
        public long Started = Environment.TickCount64;
        public bool Waiting;
        public SourceState? Source;
        public readonly ExtentWriter Extents = new();
    }

    private sealed class FatalDownload : Exception
    {
        public string Code { get; }

        public FatalDownload(string code, string message) : base(message) => Code = code;
    }

    private const long MinChunk = 8 * 1024 * 1024;
    private const long MaxChunk = 512 * 1024 * 1024;

    private readonly DownloadSpec _spec;
    private readonly object _gate = new();
    private readonly List<Worker> _workers = new();
    private readonly List<Extent> _retired = new();
    private readonly List<Extent> _extents = new();
    private readonly Dictionary<string, NetLink> _links = new();
    private readonly Dictionary<string, long> _linkDownUntil = new();
    private readonly Dictionary<string, int> _linkFailures = new();
    private readonly Dictionary<string, double> _linkSpeeds = new();
    private readonly Dictionary<string, long> _linkDownCooldown = new();
    private readonly List<SourceState> _sources = new();
    private readonly TokenBucket _ownLimit;
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private SegmentMap? _map;
    private ConnectionController? _controller;
    private SafeFileHandle? _handle;
    private Journal? _journal;
    private JournalHeader? _header;
    private ProbeResult? _probe;
    private CancellationTokenSource? _run;
    private FatalDownload? _fatal;
    private HttpFailure? _lastFailure;
    private volatile bool _fallbackSingle;
    private volatile bool _changed;
    private int _rangeErrors;
    private int _nextWorker;
    private long _lastProgress;
    private long _sessionStart;
    private long _sessionReceived;
    private long _sessionDoneBase;
    private long _transferEnd;
    private double _speed;
    private double _max;
    private long _lastThrottle;
    private long _diskTicks;
    private long _netTicks;
    private long _lastDiskTicks;
    private long _lastNetTicks;
    private string _bottleneck = "";
    private string _phase = "connecting";
    private string _note = "";
    private long _verifyDone;
    private bool _resumable = true;
    private int _bestConnections;
    private long _size = -1;
    private long _singleDone;
    private DownloadStats _stats = new();

    public HttpDownload(DownloadSpec spec)
    {
        _spec = spec;
        _ownLimit = new TokenBucket(spec.Profile.LimitBps);
    }

    public DownloadStats Stats => Volatile.Read(ref _stats);

    public MapSnapshot? Snapshot() => _map?.Snapshot();

    public long LimitBps
    {
        get => _ownLimit.Rate;
        set => _ownLimit.Rate = value;
    }

    public ProbeResult? Probe => _probe;

    public async Task<DownloadResult> RunAsync(CancellationToken ct)
    {
        _sessionStart = Environment.TickCount64;
        _lastProgress = _sessionStart;
        try
        {
            RefreshLinks();
            var restarts = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await PrepareSourcesAsync(ct);
                DownloadResult? result;
                if (_probe!.Ranges && _probe.Size > 0)
                {
                    result = await RunParallelAsync(ct);
                    if (result == null && _changed && restarts++ < 2)
                    {
                        _changed = false;
                        _note = "O arquivo mudou no servidor. O download recomeçou do zero.";
                        DiscardPartial();
                        continue;
                    }
                    if (result == null && _changed)
                        return new DownloadResult(DownloadEnd.Failed, "file-changed", "O arquivo mudou no servidor durante o download.");
                    if (result == null && _fallbackSingle)
                    {
                        _note = "O servidor não respeitou pedidos parciais. Baixando por uma conexão só.";
                        DiscardPartial();
                        result = await RunSingleAsync(ct);
                    }
                }
                else
                {
                    result = await RunSingleAsync(ct);
                }
                return result ?? new DownloadResult(DownloadEnd.Failed, "internal", "O download terminou sem resultado.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new DownloadResult(DownloadEnd.Paused, Size: _size);
        }
        catch (HttpFailure failure)
        {
            return FromFailure(failure);
        }
        catch (FatalDownload fatal)
        {
            return new DownloadResult(DownloadEnd.Failed, fatal.Code, fatal.Message);
        }
        catch (HttpRequestException e)
        {
            return new DownloadResult(DownloadEnd.Failed, "network", DescribeNetwork(e));
        }
        catch (IOException e)
        {
            return new DownloadResult(DownloadEnd.Failed, "disk-error", $"Falha ao gravar o arquivo: {e.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            return new DownloadResult(DownloadEnd.Failed, "disk-denied", "O Windows negou a gravação nessa pasta. Escolha outra pasta de destino.");
        }
        finally
        {
            CloseFile();
        }
    }

    private static DownloadResult FromFailure(HttpFailure failure) => failure.Code switch
    {
        "needs-auth" => new DownloadResult(DownloadEnd.NeedsAuth, failure.Code, failure.Message),
        "forbidden" or "gone" or "link-expired" => new DownloadResult(DownloadEnd.NeedsLink, failure.Code, failure.Message),
        _ => new DownloadResult(DownloadEnd.Failed, failure.Code, failure.Message),
    };

    private static string DescribeNetwork(HttpRequestException e) => e.InnerException is SocketException socket
        ? socket.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => "Não foi possível encontrar o servidor. Confira o endereço e a conexão.",
            SocketError.ConnectionRefused => "O servidor recusou a conexão.",
            SocketError.TimedOut => "O servidor não respondeu a tempo.",
            _ => "A conexão com o servidor falhou.",
        }
        : "A conexão com o servidor falhou.";

    private NetLink FirstLink()
    {
        lock (_gate) return _links.Values.First();
    }

    private void RefreshLinks()
    {
        var found = _spec.Links?.Invoke() ?? Array.Empty<NetLink>();
        lock (_gate)
        {
            var next = new Dictionary<string, NetLink>();
            if (found.Count == 0) next["default"] = new NetLink { Id = "default", Name = "Rede padrão" };
            else if (found.Count == 1 && found[0].Kind != "test") next[found[0].Id] = found[0] with { LocalV4 = null, LocalV6 = null };
            else foreach (var link in found) next[link.Id] = link;
            _links.Clear();
            foreach (var pair in next) _links[pair.Key] = pair.Value;
        }
    }

    private async Task PrepareSourcesAsync(CancellationToken ct)
    {
        _phase = "connecting";
        Publish();
        _sources.Clear();
        ProbeResult? primary = null;
        var client = _spec.Pool.Get(FirstLink());
        HttpFailure? firstFailure = null;
        foreach (var source in _spec.Sources)
        {
            ProbeResult result;
            try
            {
                result = await ProbeWithRetryAsync(client, source, ct);
            }
            catch (HttpFailure failure) when (_spec.Sources.Count > 1)
            {
                firstFailure ??= failure;
                continue;
            }
            catch (HttpRequestException) when (_spec.Sources.Count > 1)
            {
                continue;
            }
            if (primary == null)
            {
                primary = result;
            }
            else
            {
                if (result.Size != primary.Size || !result.Ranges || !await SameContentAsync(client, primary, source, result, ct))
                {
                    _note = $"O mirror {result.Server} foi ignorado porque não tem o mesmo arquivo.";
                    continue;
                }
            }
            _sources.Add(new SourceState { Spec = source, Final = result.FinalUrl, Same = result.SameOriginAsRequest, Etag = result.Etag });
        }
        if (primary == null) throw firstFailure ?? new HttpFailure("network", "Nenhum dos endereços respondeu.");
        _probe = primary;
        _size = primary.Size;
        _resumable = primary.Ranges;
        _spec.OnProbed?.Invoke(primary);
        if (_spec.ExpectedSize > 0 && primary.Size > 0 && _spec.ExpectedSize != primary.Size)
            _note = "O tamanho do arquivo no servidor mudou. O download recomeçou do zero.";
    }

    private async Task<ProbeResult> ProbeWithRetryAsync(HttpMessageInvoker client, SourceSpec source, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await HttpProtocol.ProbeAsync(client, source.Url, source.Auth, ct);
            }
            catch (HttpFailure failure) when (failure.Code == "throttled" && attempt < 4)
            {
                var wait = Math.Max(failure.RetryAfterMs, 1000L << attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(wait + Random.Shared.Next(250)), ct);
            }
            catch (HttpRequestException) when (attempt < 2 && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500L << attempt), ct);
            }
            catch (OperationCanceledException) when (attempt < 2 && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500L << attempt), ct);
            }
        }
    }

    private static async Task<bool> SameContentAsync(HttpMessageInvoker client, ProbeResult primary, SourceSpec mirror, ProbeResult mirrorProbe, CancellationToken ct)
    {
        var length = Math.Min(64 * 1024L, primary.Size);
        var a = await SampleAsync(client, primary.FinalUrl, null, length, ct);
        var b = await SampleAsync(client, mirrorProbe.FinalUrl, mirrorProbe.SameOriginAsRequest ? mirror.Auth : null, length, ct);
        return a != null && b != null && a.AsSpan().SequenceEqual(b);
    }

    private static async Task<byte[]?> SampleAsync(HttpMessageInvoker client, Uri url, RequestAuth? auth, long length, CancellationToken ct)
    {
        try
        {
            using var request = HttpProtocol.Build(HttpMethod.Get, url, auth, true);
            request.Headers.TryAddWithoutValidation("Range", $"bytes=0-{length - 1}");
            using var response = await client.SendAsync(request, ct);
            if ((int)response.StatusCode != 206) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length == length ? bytes : null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return null;
        }
    }

    private void OpenFile(long size, bool sparse)
    {
        Directory.CreateDirectory(_spec.Folder);
        if (!File.Exists(_spec.PartPath))
        {
            using var _ = File.Create(_spec.PartPath);
        }
        if (sparse) NativeDisk.SetSparse(_spec.PartPath, true);
        _handle = File.OpenHandle(_spec.PartPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        if (size >= 0) RandomAccess.SetLength(_handle, size);
    }

    private void CloseFile()
    {
        _journal?.Dispose();
        _journal = null;
        _handle?.Dispose();
        _handle = null;
    }

    private void DiscardPartial()
    {
        CloseFile();
        lock (_gate)
        {
            _extents.Clear();
            _retired.Clear();
        }
        _map = null;
        TryDelete(_spec.PartPath);
        TryDelete(_spec.JournalPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<DownloadResult?> RunParallelAsync(CancellationToken ct)
    {
        var probe = _probe!;
        var size = probe.Size;
        var etag = probe.WeakEtag ? "" : probe.Etag;
        _header = new JournalHeader(size, etag, probe.LastModified);
        await LoadOrCreateAsync(size, ct);
        for (var round = 0; round < 3; round++)
        {
            var ended = await PumpAsync(size, ct);
            if (ended != null) return ended.Value ? new DownloadResult(DownloadEnd.Paused, Size: size) : null;
            _speed = 0;
            if (!_spec.Profile.Verify) return await FinishAsync(size, true, ct);
            _phase = "verifying";
            _verifyDone = 0;
            Publish();
            List<Extent> snapshot;
            lock (_gate) snapshot = _extents.ToList();
            var bad = await ExtentVerifier.FindBadAsync(_handle!, snapshot, 4, n => Interlocked.Add(ref _verifyDone, n), ct);
            var covered = new RangeList();
            foreach (var extent in snapshot) if (!bad.Contains(extent)) covered.Add(extent.Start, extent.End);
            if (bad.Count == 0 && covered.Total == size) return await FinishAsync(size, true, ct);
            lock (_gate) _extents.RemoveAll(e => bad.Contains(e));
            var cursor = 0L;
            foreach (var (s, e) in covered.Items)
            {
                if (s > cursor) _map!.Invalidate(cursor, s);
                cursor = e;
            }
            if (cursor < size) _map!.Invalidate(cursor, size);
            lock (_gate) _journal?.Rewrite(_spec.JournalPath, _header, _extents);
            _note = "Alguns trechos gravados não conferiram e estão sendo baixados de novo.";
        }
        return new DownloadResult(DownloadEnd.Failed, "verify-failed", "Os dados gravados continuam sem conferir depois de 3 tentativas. Verifique o disco de destino.");
    }

    private async Task LoadOrCreateAsync(long size, CancellationToken ct)
    {
        var opened = File.Exists(_spec.PartPath) ? Journal.Open(_spec.JournalPath, _header!) : null;
        if (opened != null && new FileInfo(_spec.PartPath).Length == size)
        {
            OpenFile(size, false);
            _journal = opened.Value.Journal;
            var extents = opened.Value.Extents;
            var recent = extents.TakeLast(16).ToList();
            var bad = await ExtentVerifier.FindBadAsync(_handle!, recent, 4, null, ct);
            if (bad.Count > 0)
            {
                extents.RemoveAll(e => bad.Contains(e));
                _journal.Rewrite(_spec.JournalPath, _header!, extents);
                _note = "Alguns trechos salvos não conferiram e serão baixados de novo.";
            }
            lock (_gate) _extents.AddRange(extents);
            _map = new SegmentMap(size, extents.Select(e => (e.Start, e.End)));
        }
        else
        {
            opened?.Journal.Dispose();
            TryDelete(_spec.PartPath);
            var free = NativeDisk.FreeSpace(_spec.Folder);
            if (free >= 0 && free < size) throw new FatalDownload("disk-full", $"Faltam {Format.Bytes(size - free)} no disco de destino. Libere espaço ou escolha outra pasta.");
            OpenFile(size, true);
            _journal = Journal.Create(_spec.JournalPath, _header!);
            lock (_gate) _extents.Clear();
            _map = new SegmentMap(size);
        }
        _sessionDoneBase = _map.DoneBytes;
        var remaining = size - _map.DoneBytes;
        var freeNow = NativeDisk.FreeSpace(_spec.Folder);
        if (freeNow >= 0 && freeNow < remaining) throw new FatalDownload("disk-full", $"Faltam {Format.Bytes(remaining - freeNow)} no disco de destino. Libere espaço ou escolha outra pasta.");
        var profile = _spec.Profile;
        var memory = _spec.Hosts?.Get(probe_host());
        var start = size < 8 * 1024 * 1024 ? 1 : size < 32 * 1024 * 1024 ? Math.Min(2, profile.StartConnections) : profile.StartConnections;
        if (memory is { Best: > 1 }) start = Math.Max(start, Math.Min(memory.Best / 2, profile.MaxConnections));
        var max = profile.MaxConnections;
        if (memory is { Cap: > 0 } && profile.FixedConnections == null) max = Math.Min(max, Math.Max(memory.Cap, 1));
        start = Math.Clamp(start, 1, max);
        var options = new ControllerOptions
        {
            Start = start,
            Max = max,
            MaxStep = profile.MaxStep,
            GainThreshold = profile.GainThreshold,
            SettleMs = profile.SettleMs,
            WindowTicks = profile.Mode == "eco" ? 5 : 4,
            Fixed = profile.FixedConnections,
            AutoSecondaryLinks = profile.MultiLink,
        };
        string[] ids;
        lock (_gate) ids = _links.Keys.ToArray();
        _controller = new ConnectionController(options, ids);
    }

    private string probe_host() => _probe?.FinalUrl.Host ?? "";

    private async Task<bool?> PumpAsync(long size, CancellationToken ct)
    {
        _phase = "downloading";
        _transferEnd = 0;
        _run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lastCheckpoint = Environment.TickCount64;
        var lastTick = lastCheckpoint;
        var lastLinks = lastCheckpoint;
        var lastSlow = lastCheckpoint;
        var checkpointBytes = _map!.DoneBytes;
        Task checkpoint = Task.CompletedTask;
        var tick = 0;
        while (true)
        {
            var now = Environment.TickCount64;
            if (_map.IsComplete) break;
            if (ct.IsCancellationRequested || _fatal != null || _fallbackSingle || _changed) break;
            if (_lastFailure != null && NoUsableSources()) break;
            if (now - _lastProgress > _spec.NoProgressMs)
            {
                _fatal = new FatalDownload(_lastFailure?.Code ?? "network", _lastFailure?.Message ?? "O download ficou parado por muito tempo sem receber dados.");
                break;
            }
            if (now - lastLinks >= 2000)
            {
                lastLinks = now;
                RefreshLinksDuring(now);
            }
            if (now - lastSlow >= 2000)
            {
                lastSlow = now;
                RetireSlow(now);
            }
            Balance(now);
            await Task.Delay(100, CancellationToken.None);
            tick++;
            if (tick % 2 == 0)
            {
                var elapsed = Environment.TickCount64 - lastTick;
                lastTick = Environment.TickCount64;
                Sample(elapsed, size);
            }
            var doneNow = _map.DoneBytes;
            if (checkpoint.IsCompleted && (now - lastCheckpoint >= 5000 || doneNow - checkpointBytes >= 128 * 1024 * 1024))
            {
                lastCheckpoint = now;
                checkpointBytes = doneNow;
                checkpoint = Task.Run(CheckpointAsync, CancellationToken.None);
            }
        }
        _transferEnd = Environment.TickCount64;
        _run.Cancel();
        List<Task> tasks;
        lock (_gate) tasks = _workers.Select(w => w.Task).ToList();
        await Task.WhenAll(tasks);
        await checkpoint;
        await CheckpointAsync();
        lock (_gate) _workers.Clear();
        _run.Dispose();
        _run = null;
        if (_fatal != null) throw _fatal;
        if (ct.IsCancellationRequested && !_map.IsComplete)
        {
            Sample(250, size);
            return true;
        }
        if (_fallbackSingle || _changed) return false;
        if (!_map.IsComplete)
        {
            if (_lastFailure != null) throw _lastFailure;
            throw new HttpFailure("network", "Nenhuma fonte do arquivo está respondendo.");
        }
        return null;
    }

    private bool NoUsableSources()
    {
        lock (_gate) return _sources.All(s => s.Dead);
    }

    private void RefreshLinksDuring(long now)
    {
        RefreshLinks();
        string[] ids;
        lock (_gate)
        {
            foreach (var pair in _linkDownUntil.ToArray())
            {
                if (pair.Value > now) continue;
                _linkDownUntil.Remove(pair.Key);
                _controller!.SetLinkDown(pair.Key, false, now);
            }
            ids = _links.Keys.Where(id => !_linkDownUntil.ContainsKey(id)).ToArray();
        }
        _controller!.SetLinks(ids, now);
    }

    private void Balance(long now)
    {
        var controller = _controller!;
        List<NetLink> links;
        lock (_gate) links = _links.Values.ToList();
        var minSplit = EffectiveMinSplit();
        var remaining = _map!.Size - _map.DoneBytes;
        var eta = _speed > 0 ? remaining / _speed : double.MaxValue;
        foreach (var link in links)
        {
            var target = controller.TargetFor(link.Id);
            List<Worker> mine;
            lock (_gate) mine = _workers.Where(w => w.Link.Id == link.Id && !w.Cts.IsCancellationRequested).ToList();
            var totalActive = _map.ActiveCount;
            for (var i = mine.Count; i < target; i++)
            {
                if (totalActive > 0 && (_map.LargestAvailable < minSplit || eta < 1.0)) break;
                Spawn(link);
                totalActive++;
            }
            if (mine.Count > target)
            {
                foreach (var worker in mine.OrderBy(w => w.Speed).Take(mine.Count - target))
                    worker.Cts.Cancel();
            }
        }
        lock (_gate)
        {
            foreach (var worker in _workers)
            {
                if (worker.Waiting || worker.Cts.IsCancellationRequested || worker.Segment == null) continue;
                if (now - worker.LastByte > _spec.StallMs)
                {
                    worker.Cts.Cancel();
                    controller.OnErrors(worker.Link.Id, 1, now);
                }
            }
            _workers.RemoveAll(w => w.Task.IsCompleted);
        }
    }

    private long EffectiveMinSplit()
    {
        var floor = Math.Min(_spec.Profile.MinFragment, Math.Max(256 * 1024, _map!.Size / 64));
        var active = Math.Max(1, _map.ActiveCount);
        return Math.Max(floor, (long)(_speed / active * 0.5));
    }

    private void Spawn(NetLink link)
    {
        var worker = new Worker { Id = Interlocked.Increment(ref _nextWorker), Link = link, Cts = CancellationTokenSource.CreateLinkedTokenSource(_run!.Token) };
        lock (_gate) _workers.Add(worker);
        worker.Task = Task.Run(() => WorkerLoopAsync(worker), CancellationToken.None);
    }

    private void RetireSlow(long now)
    {
        List<Worker> list;
        lock (_gate) list = _workers.Where(w => !w.Cts.IsCancellationRequested && w.Segment != null).ToList();
        foreach (var group in list.GroupBy(w => w.Link.Id))
        {
            var items = group.OrderBy(w => w.Speed).ToList();
            if (items.Count < 3) continue;
            var median = items[items.Count / 2].Speed;
            var slowest = items[0];
            if (now - slowest.Started > 8000 && slowest.Speed < median * 0.25)
                slowest.Cts.Cancel();
        }
    }

    private async Task WorkerLoopAsync(Worker worker)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_spec.Profile.BufferBytes);
        var token = worker.Cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var minSplit = EffectiveMinSplit();
                var seg = _map!.Acquire(minSplit, worker.Link.Id, worker.Id);
                if (seg == null)
                {
                    var victim = _map.FindTakeover(worker.Speed, worker.Id);
                    if (victim >= 0)
                    {
                        lock (_gate) _workers.FirstOrDefault(w => w.Id == victim)?.Cts.Cancel();
                        await Task.Delay(50, token);
                        continue;
                    }
                    await Task.Delay(100, token);
                    if (_map.LargestAvailable < minSplit && _map.ActiveCount <= 1) break;
                    if (_map.LargestAvailable < minSplit) break;
                    continue;
                }
                worker.Segment = seg;
                try
                {
                    await RunSegmentAsync(worker, seg, buffer, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpFailure failure)
                {
                    await HandleFailureAsync(worker, failure, token);
                }
                catch (Exception e) when (e is HttpRequestException or IOException or SocketException or OperationCanceledException or TimeoutException)
                {
                    await HandleTransientAsync(worker, token);
                }
                finally
                {
                    _map.Release(seg);
                    worker.Segment = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (FatalDownload fatal)
        {
            _fatal ??= fatal;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _fatal ??= new FatalDownload("disk-error", $"Falha ao gravar o arquivo: {e.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            Retire(worker);
        }
    }

    private void Retire(Worker worker)
    {
        var drained = new List<Extent>();
        worker.Extents.Drain(drained);
        lock (_gate) _retired.AddRange(drained);
    }

    private SourceState? PickSource(long now)
    {
        lock (_gate)
        {
            SourceState? best = null;
            var bestScore = double.MaxValue;
            foreach (var source in _sources)
            {
                if (source.Dead || source.CooldownUntil > now) continue;
                var score = (source.Active + 1) / Math.Max(source.Speed, 1024 * 1024);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = source;
                }
            }
            return best;
        }
    }

    private async Task RunSegmentAsync(Worker worker, Segment seg, byte[] buffer, CancellationToken ct)
    {
        var client = _spec.Pool.Get(worker.Link);
        while (true)
        {
            var end = _map!.EndOf(seg);
            if (seg.Pos >= end) return;
            var now = Environment.TickCount64;
            var source = PickSource(now);
            if (source == null)
            {
                worker.Waiting = true;
                await Task.Delay(200, ct);
                worker.Waiting = false;
                continue;
            }
            var chunk = (long)Math.Clamp(worker.Speed * 10, MinChunk, MaxChunk);
            var requestEnd = Math.Min(end, seg.Pos + chunk);
            seg.RequestEnd = requestEnd;
            lock (_gate) source.Active++;
            worker.Source = source;
            try
            {
                using var request = HttpProtocol.Build(HttpMethod.Get, source.Final, source.Spec.Auth, source.Same);
                request.Headers.TryAddWithoutValidation("Range", $"bytes={seg.Pos}-{requestEnd - 1}");
                if (source.Etag.Length > 0 && !source.Etag.StartsWith("W/", StringComparison.Ordinal))
                    request.Headers.TryAddWithoutValidation("If-Range", source.Etag);
                else if (_probe!.LastModified.Length > 0 && _spec.Sources.Count == 1)
                    request.Headers.TryAddWithoutValidation("If-Range", _probe.LastModified);
                worker.Waiting = true;
                using var response = await client.SendAsync(request, ct);
                worker.Waiting = false;
                worker.LastByte = Environment.TickCount64;
                var limit = ValidateResponse(response, source, seg, requestEnd);
                if (limit < 0) return;
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await PumpStreamAsync(worker, seg, source, stream, buffer, limit, ct);
                lock (_gate) source.Failures = 0;
            }
            finally
            {
                worker.Waiting = false;
                lock (_gate) source.Active--;
            }
        }
    }

    private long ValidateResponse(HttpResponseMessage response, SourceState source, Segment seg, long requestEnd)
    {
        var status = (int)response.StatusCode;
        if (status == 206)
        {
            var range = response.Content.Headers.ContentRange;
            if (range is not { HasRange: true, HasLength: true } || range.From != seg.Pos || range.To >= requestEnd || range.To < seg.Pos || range.Length != _map!.Size)
            {
                if (Interlocked.Increment(ref _rangeErrors) >= 3)
                {
                    _fallbackSingle = true;
                    _run?.Cancel();
                }
                throw new HttpFailure("invalid-range", "O servidor devolveu um trecho diferente do pedido.", status);
            }
            CheckEtag(response, source);
            return range.To!.Value + 1;
        }
        if (status == 200)
        {
            CheckEtag(response, source);
            if (source.Etag.Length > 0 || _probe!.LastModified.Length > 0)
            {
                var modified = response.Content.Headers.LastModified?.UtcDateTime.ToString("R") ?? "";
                if (source.Etag.Length == 0 && modified.Length > 0 && modified != _probe!.LastModified)
                {
                    _changed = true;
                    _run?.Cancel();
                    throw new HttpFailure("file-changed", "O arquivo mudou no servidor.", status);
                }
            }
            _fallbackSingle = true;
            _run?.Cancel();
            throw new HttpFailure("invalid-range", "O servidor ignorou o pedido parcial.", status);
        }
        HttpProtocol.Ensure(response);
        throw new HttpFailure("http", $"Resposta inesperada do servidor ({status}).", status);
    }

    private void CheckEtag(HttpResponseMessage response, SourceState source)
    {
        var etag = response.Headers.ETag?.ToString() ?? "";
        if (etag.Length == 0 || source.Etag.Length == 0 || etag == source.Etag) return;
        _changed = true;
        _run?.Cancel();
        throw new HttpFailure("file-changed", "O arquivo mudou no servidor.", (int)response.StatusCode);
    }

    private async Task PumpStreamAsync(Worker worker, Segment seg, SourceState source, Stream stream, byte[] buffer, long limit, CancellationToken ct)
    {
        var capacity = Math.Min(buffer.Length, _spec.Profile.BufferBytes);
        var filled = 0;
        var lastFlush = Stopwatch.GetTimestamp();
        var flushInterval = Stopwatch.Frequency / 5;
        try
        {
            while (true)
            {
                var want = capacity - filled;
                var left = limit - seg.Pos - filled;
                if (left <= 0) break;
                if (want > left) want = (int)left;
                want = Math.Min(want, _ownLimit.MaxChunk);
                if (_spec.GlobalLimit != null) want = Math.Min(want, _spec.GlobalLimit.MaxChunk);
                var startRead = Stopwatch.GetTimestamp();
                var read = await stream.ReadAsync(buffer.AsMemory(filled, want), ct);
                Interlocked.Add(ref _netTicks, Stopwatch.GetTimestamp() - startRead);
                if (read <= 0) throw new IOException("A conexão terminou antes do fim do trecho.");
                filled += read;
                worker.LastByte = Environment.TickCount64;
                Interlocked.Add(ref worker.Received, read);
                Interlocked.Add(ref source.Received, read);
                _lastProgress = worker.LastByte;
                await _ownLimit.TakeAsync(read, ct);
                if (_spec.GlobalLimit != null) await _spec.GlobalLimit.TakeAsync(read, ct);
                if (filled >= capacity || Stopwatch.GetTimestamp() - lastFlush >= flushInterval)
                {
                    var keep = await FlushAsync(worker, seg, buffer, filled, ct);
                    filled = 0;
                    lastFlush = Stopwatch.GetTimestamp();
                    if (!keep) return;
                }
            }
            if (filled > 0)
            {
                await FlushAsync(worker, seg, buffer, filled, ct);
                filled = 0;
            }
        }
        finally
        {
            if (filled > 0) await FlushAsync(worker, seg, buffer, filled, CancellationToken.None);
        }
    }

    private async Task<bool> FlushAsync(Worker worker, Segment seg, byte[] buffer, int count, CancellationToken ct)
    {
        var allowed = _map!.Reserve(seg, count);
        if (allowed > 0)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                await RandomAccess.WriteAsync(_handle!, buffer.AsMemory(0, allowed), seg.Pos, CancellationToken.None);
            }
            catch (IOException e) when ((e.HResult & 0xFFFF) is 112 or 39)
            {
                throw new FatalDownload("disk-full", "O disco de destino ficou cheio. Libere espaço e continue o download.");
            }
            Interlocked.Add(ref _diskTicks, Stopwatch.GetTimestamp() - start);
            worker.Extents.Append(seg.Pos, buffer.AsSpan(0, allowed));
            _map.Commit(seg, allowed);
        }
        return allowed == count;
    }

    private async Task CheckpointAsync()
    {
        await _checkpointGate.WaitAsync();
        try
        {
            var fresh = new List<Extent>();
            List<Worker> workers;
            lock (_gate)
            {
                workers = _workers.ToList();
                fresh.AddRange(_retired);
                _retired.Clear();
            }
            foreach (var worker in workers) worker.Extents.Drain(fresh);
            if (fresh.Count == 0 || _handle == null) return;
            var durable = !_spec.Profile.Verify;
            if (durable) RandomAccess.FlushToDisk(_handle);
            _journal?.Append(fresh, durable);
            lock (_gate) _extents.AddRange(fresh);
        }
        catch (IOException)
        {
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    private async Task HandleFailureAsync(Worker worker, HttpFailure failure, CancellationToken ct)
    {
        var now = Environment.TickCount64;
        _lastFailure = failure;
        var controller = _controller!;
        switch (failure.Code)
        {
            case "throttled":
                _lastThrottle = now;
                var wait = failure.RetryAfterMs > 0 ? failure.RetryAfterMs : Math.Min(2000L << Math.Min(ThrottleCount(), 6), 60000);
                wait += Random.Shared.Next(250, 1000);
                controller.OnThrottle(worker.Link.Id, now, wait);
                lock (_gate)
                    foreach (var source in _sources)
                    {
                        source.CooldownUntil = Math.Max(source.CooldownUntil, now + wait);
                        source.Failures++;
                    }
                _spec.Hosts?.RecordCap(probe_host(), Math.Max(1, controller.TotalTarget));
                _lastProgress = Math.Max(_lastProgress, now);
                await WaitAsync(worker, wait, ct);
                break;
            case "forbidden" or "gone" or "needs-auth" or "not-found":
                await HandleDeniedAsync(worker, failure, ct);
                break;
            case "file-changed" or "invalid-range":
                await WaitAsync(worker, 200, ct);
                break;
            default:
                await HandleTransientAsync(worker, ct);
                break;
        }
    }

    private int ThrottleCount()
    {
        lock (_gate) return _sources.Count == 0 ? 0 : _sources.Max(s => s.Failures);
    }

    private async Task HandleDeniedAsync(Worker worker, HttpFailure failure, CancellationToken ct)
    {
        SourceState? source;
        int activeOthers;
        lock (_gate)
        {
            source = worker.Source ?? _sources.FirstOrDefault(s => !s.Dead);
            activeOthers = _workers.Count(w => w.Id != worker.Id && Environment.TickCount64 - w.LastByte < 3000 && w.Segment != null);
        }
        if (source == null) return;
        if (activeOthers > 0 && failure.Code == "forbidden")
        {
            _controller!.OnConnectionLimit(worker.Link.Id, Environment.TickCount64);
            _spec.Hosts?.RecordCap(probe_host(), Math.Max(1, _controller.TotalTarget));
            await WaitAsync(worker, 1500, ct);
            return;
        }
        await source.ResolveGate.WaitAsync(ct);
        try
        {
            var now = Environment.TickCount64;
            if (now - source.LastResolve < 3000)
            {
                await WaitAsync(worker, 300, ct);
                return;
            }
            source.LastResolve = now;
            try
            {
                var probe = await HttpProtocol.ProbeAsync(_spec.Pool.Get(worker.Link), source.Spec.Url, source.Spec.Auth, ct);
                if (probe.Size == _map!.Size && probe.Ranges && probe.FinalUrl != source.Final)
                {
                    source.Final = probe.FinalUrl;
                    source.Same = probe.SameOriginAsRequest;
                    return;
                }
                if (probe.Size == _map.Size && probe.Ranges && probe.FinalUrl == source.Final)
                {
                    lock (_gate) source.CooldownUntil = Environment.TickCount64 + 2000;
                    if (++source.Failures < 5) return;
                }
            }
            catch (HttpFailure next)
            {
                failure = next;
            }
            catch (HttpRequestException)
            {
                await WaitAsync(worker, 1000, ct);
                return;
            }
            lock (_gate) source.Dead = true;
            _lastFailure = failure.Code is "needs-auth" or "not-found" ? failure : new HttpFailure("link-expired", "O link expirou. Cole um link novo para continuar de onde parou.", failure.Status);
        }
        finally
        {
            source.ResolveGate.Release();
        }
    }

    private async Task HandleTransientAsync(Worker worker, CancellationToken ct)
    {
        var now = Environment.TickCount64;
        var link = worker.Link.Id;
        int failures;
        lock (_gate)
        {
            _linkFailures.TryGetValue(link, out failures);
            failures++;
            _linkFailures[link] = failures;
        }
        _controller!.OnErrors(link, 1, now);
        if (failures >= 4 && worker.Link.Kind != "default" && _links.Count > 1)
        {
            lock (_gate)
            {
                var active = _links.Keys.Count(id => !_linkDownUntil.ContainsKey(id));
                if (active > 1)
                {
                    _linkDownCooldown.TryGetValue(link, out var cooldown);
                    cooldown = Math.Min(Math.Max(cooldown * 2, 3000), 60000);
                    _linkDownCooldown[link] = cooldown;
                    _linkDownUntil[link] = now + cooldown;
                    _linkFailures[link] = 0;
                    _controller.SetLinkDown(link, true, now);
                }
            }
        }
        var delay = Math.Min(250L << Math.Min(failures, 6), 15000) + Random.Shared.Next(0, 250);
        await WaitAsync(worker, delay, ct);
    }

    private static async Task WaitAsync(Worker worker, long ms, CancellationToken ct)
    {
        worker.Waiting = true;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
        }
        finally
        {
            worker.Waiting = false;
        }
    }

    private void Sample(long elapsedMs, long size)
    {
        if (elapsedMs <= 0) return;
        var now = Environment.TickCount64;
        var seconds = elapsedMs / 1000.0;
        double total = 0;
        var perLink = new Dictionary<string, double>();
        var connections = new Dictionary<string, int>();
        List<Worker> workers;
        lock (_gate)
        {
            workers = _workers.ToList();
            foreach (var worker in workers)
            {
                var received = Interlocked.Read(ref worker.Received);
                var delta = received - worker.LastSample;
                worker.LastSample = received;
                var speed = delta / seconds;
                worker.Speed = worker.Speed <= 0 ? speed : worker.Speed * 0.7 + speed * 0.3;
                if (worker.Segment is { } seg) seg.Speed = worker.Speed;
                total += delta;
                perLink[worker.Link.Id] = perLink.GetValueOrDefault(worker.Link.Id) + delta / seconds;
                connections[worker.Link.Id] = connections.GetValueOrDefault(worker.Link.Id) + (worker.Segment != null ? 1 : 0);
                if (delta > 0) _linkFailures[worker.Link.Id] = 0;
            }
            foreach (var source in _sources)
            {
                var received = Interlocked.Read(ref source.Received);
                var speed = (received - source.LastReceived) / seconds;
                source.LastReceived = received;
                source.Speed = source.Speed * 0.7 + speed * 0.3;
            }
            foreach (var pair in perLink)
                _linkSpeeds[pair.Key] = _linkSpeeds.GetValueOrDefault(pair.Key) * 0.5 + pair.Value * 0.5;
        }
        _sessionReceived += (long)total;
        var instant = total / seconds;
        _speed = _speed <= 0 ? instant : _speed * 0.6 + instant * 0.4;
        _max = Math.Max(_max, _speed);
        var disk = Interlocked.Read(ref _diskTicks);
        var net = Interlocked.Read(ref _netTicks);
        var diskDelta = disk - _lastDiskTicks;
        var netDelta = net - _lastNetTicks;
        _lastDiskTicks = disk;
        _lastNetTicks = net;
        var diskBound = diskDelta + netDelta > 0 && diskDelta > 0.55 * (diskDelta + netDelta);
        var limitBound = IsLimited(elapsedMs);
        var controller = _controller;
        var nearEnd = _map != null && _map.LargestAvailable < EffectiveMinSplit();
        if (controller != null)
        {
            controller.Tick(now, instant, perLink, diskBound || limitBound || nearEnd);
            _bestConnections = Math.Max(_bestConnections, workers.Count);
            if (!limitBound && !diskBound && controller.Saturated) _bestConnections = controller.TotalTarget;
        }
        _bottleneck = now - _lastThrottle < 30000 ? "server-limit"
            : limitBound ? "speed-limit"
            : diskBound ? "disk"
            : LinkAtCapacity(perLink) ? "link"
            : controller is { Saturated: true } ? "saturated"
            : "";
        Publish(connections);
    }

    private long _lastWaited;

    private bool IsLimited(long elapsedMs)
    {
        var waited = _ownLimit.WaitedMs + (_spec.GlobalLimit?.WaitedMs ?? 0);
        var delta = waited - _lastWaited;
        _lastWaited = waited;
        return delta > elapsedMs * 0.2;
    }

    private bool LinkAtCapacity(Dictionary<string, double> perLink)
    {
        lock (_gate)
        {
            foreach (var pair in perLink)
                if (_links.TryGetValue(pair.Key, out var link) && link.NominalBps > 0 && pair.Value >= link.NominalBps * 0.85)
                    return true;
        }
        return false;
    }

    private void Publish(IReadOnlyDictionary<string, int>? connections = null)
    {
        var now = Environment.TickCount64;
        var map = _map;
        var size = _size;
        var done = map?.DoneBytes ?? Interlocked.Read(ref _singleDone);
        var elapsed = Math.Max(1, now - _sessionStart) / 1000.0;
        var eta = _speed > 1 && size > 0 ? (size - done) / _speed : -1;
        var links = new List<LinkStat>();
        lock (_gate)
        {
            foreach (var link in _links.Values)
            {
                var control = _controller?.Links.FirstOrDefault(l => l.Id == link.Id);
                links.Add(new LinkStat(link.Id, link.Name, _linkSpeeds.GetValueOrDefault(link.Id), connections?.GetValueOrDefault(link.Id) ?? 0, control?.NoGain ?? false));
            }
        }
        Volatile.Write(ref _stats, new DownloadStats
        {
            Phase = _phase,
            Done = done,
            Size = size,
            Speed = _speed,
            Average = _sessionReceived / elapsed,
            Max = _max,
            EtaSeconds = eta,
            Connections = connections?.Values.Sum() ?? 0,
            Target = _controller?.TotalTarget ?? 1,
            Fragments = map?.FragmentCount ?? 1,
            Bottleneck = _bottleneck,
            Resumable = _resumable,
            Note = _note,
            VerifyDone = Interlocked.Read(ref _verifyDone),
            Links = links,
        });
    }

    private async Task<DownloadResult?> RunSingleAsync(CancellationToken ct)
    {
        _resumable = false;
        _fallbackSingle = false;
        _phase = "downloading";
        var link = FirstLink();
        var client = _spec.Pool.Get(link);
        var source = _sources.First();
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            DiscardPartial();
            OpenFile(-1, false);
            _singleDone = 0;
            var worker = new Worker { Id = 1, Link = link, Cts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
            lock (_gate)
            {
                _workers.Clear();
                _workers.Add(worker);
            }
            using var sampler = new CancellationTokenSource();
            var samplerTask = Task.Run(async () =>
            {
                var last = Environment.TickCount64;
                worker.Segment = new Segment();
                while (!sampler.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(250, sampler.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    var now = Environment.TickCount64;
                    Sample(now - last, _size);
                    last = now;
                    if (now - worker.LastByte > _spec.StallMs) worker.Cts.Cancel();
                }
            }, CancellationToken.None);
            try
            {
                var total = await StreamWholeAsync(client, source, worker, worker.Cts.Token);
                sampler.Cancel();
                await samplerTask;
                _size = total;
                RandomAccess.SetLength(_handle!, total);
                worker.Extents.Drain(_retired);
                lock (_gate)
                {
                    _extents.AddRange(_retired);
                    _retired.Clear();
                    _workers.Clear();
                }
                if (_spec.Profile.Verify)
                {
                    _phase = "verifying";
                    Publish();
                    var bad = await ExtentVerifier.FindBadAsync(_handle!, _extents, 2, null, ct);
                    if (bad.Count > 0) throw new IOException("Os dados gravados não conferem.");
                }
                return await FinishAsync(total, _spec.Profile.Verify, ct);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested && attempt < 3)
            {
                sampler.Cancel();
                await samplerTask;
                await Task.Delay(TimeSpan.FromMilliseconds((500L << attempt) + Random.Shared.Next(250)), ct);
            }
            finally
            {
                sampler.Cancel();
                await samplerTask;
                worker.Cts.Dispose();
            }
        }
    }

    private async Task<long> StreamWholeAsync(HttpMessageInvoker client, SourceState source, Worker worker, CancellationToken ct)
    {
        var (response, _, _) = await HttpProtocol.SendFollowingAsync(client, source.Spec.Url, source.Spec.Auth, null, ct);
        using (response)
        {
            HttpProtocol.Ensure(response);
            var expected = response.Content.Headers.ContentLength ?? -1;
            var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault()?.ToLowerInvariant() ?? "";
            await using var raw = await response.Content.ReadAsStreamAsync(ct);
            Stream stream = encoding switch
            {
                "gzip" or "x-gzip" => new GZipStream(raw, CompressionMode.Decompress, true),
                "deflate" => new ZLibStream(raw, CompressionMode.Decompress, true),
                "br" => new BrotliStream(raw, CompressionMode.Decompress, true),
                _ => raw,
            };
            if (encoding.Length > 0) expected = -1;
            _size = expected;
            var buffer = ArrayPool<byte>.Shared.Rent(_spec.Profile.BufferBytes);
            try
            {
                var position = 0L;
                while (true)
                {
                    var want = Math.Min(buffer.Length, _ownLimit.MaxChunk);
                    if (_spec.GlobalLimit != null) want = Math.Min(want, _spec.GlobalLimit.MaxChunk);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, want), ct);
                    if (read <= 0) break;
                    worker.LastByte = Environment.TickCount64;
                    _lastProgress = worker.LastByte;
                    Interlocked.Add(ref worker.Received, read);
                    await _ownLimit.TakeAsync(read, ct);
                    if (_spec.GlobalLimit != null) await _spec.GlobalLimit.TakeAsync(read, ct);
                    var start = Stopwatch.GetTimestamp();
                    try
                    {
                        await RandomAccess.WriteAsync(_handle!, buffer.AsMemory(0, read), position, CancellationToken.None);
                    }
                    catch (IOException e) when ((e.HResult & 0xFFFF) is 112 or 39)
                    {
                        throw new FatalDownload("disk-full", "O disco de destino ficou cheio. Libere espaço e tente de novo.");
                    }
                    Interlocked.Add(ref _diskTicks, Stopwatch.GetTimestamp() - start);
                    worker.Extents.Append(position, buffer.AsSpan(0, read));
                    position += read;
                    Interlocked.Exchange(ref _singleDone, position);
                }
                if (expected >= 0 && position != expected) throw new IOException("A conexão terminou antes do fim do arquivo.");
                return position;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                if (!ReferenceEquals(stream, raw)) await stream.DisposeAsync();
            }
        }
    }

    private async Task<DownloadResult> FinishAsync(long size, bool verified, CancellationToken ct)
    {
        _phase = "finalizing";
        Publish();
        _journal?.Dispose();
        _journal = null;
        if (_handle != null) RandomAccess.FlushToDisk(_handle);
        _handle?.Dispose();
        _handle = null;
        NativeDisk.SetSparse(_spec.PartPath, false);
        var checksumState = "";
        var checksum = !string.IsNullOrWhiteSpace(_spec.Checksum) ? _spec.Checksum : _probe?.Digest;
        if (!string.IsNullOrWhiteSpace(checksum))
        {
            _phase = "checksum";
            Publish();
            var ok = await ChecksumMatchesAsync(_spec.PartPath, checksum!, ct);
            if (ok == null)
            {
                checksumState = "unsupported";
            }
            else if (!ok.Value)
            {
                return new DownloadResult(DownloadEnd.Failed, "checksum-mismatch", "O arquivo baixado não bate com o checksum informado. Ele foi mantido como arquivo parcial e não deve ser usado.", Size: size, ChecksumState: "mismatch");
            }
            else
            {
                checksumState = "ok";
            }
        }
        var name = _spec.FileName is { Length: > 0 } fixedName ? fixedName : (_probe?.FileName ?? "download");
        string final = "";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            final = NativeDisk.UniquePath(_spec.Folder, name);
            try
            {
                File.Move(_spec.PartPath, final, false);
                break;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(200 * (attempt + 1), ct);
            }
        }
        if (_spec.MarkOfTheWeb) NativeDisk.MarkFromInternet(final);
        TryDelete(_spec.JournalPath);
        _phase = "done";
        _singleDone = size;
        var end = _transferEnd > 0 ? _transferEnd : Environment.TickCount64;
        var elapsed = Math.Max(1, end - _sessionStart) / 1000.0;
        var received = _map != null ? _map.DoneBytes - _sessionDoneBase : size;
        var average = received / elapsed;
        _max = Math.Max(_max, average);
        Publish();
        return new DownloadResult(DownloadEnd.Done, FinalPath: final, Size: size, Verified: verified, ChecksumState: checksumState, AverageBps: average, MaxBps: _max, BestConnections: Math.Max(_bestConnections, 1));
    }

    public static async Task<bool?> ChecksumMatchesAsync(string path, string checksum, CancellationToken ct)
    {
        var at = checksum.IndexOf(':');
        if (at <= 0) return null;
        var algorithm = checksum[..at].Trim().ToLowerInvariant().Replace("-", "");
        var expected = checksum[(at + 1)..].Trim().ToLowerInvariant();
        using HashAlgorithm? hash = algorithm switch
        {
            "sha256" => SHA256.Create(),
            "sha512" => SHA512.Create(),
            "sha1" => SHA1.Create(),
            "md5" => MD5.Create(),
            _ => null,
        };
        if (hash == null) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var bytes = await hash.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(bytes).ToLowerInvariant() == expected;
    }
}

public static class Format
{
    public static string Bytes(long value)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value} B" : $"{size:0.0} {units[unit]}";
    }
}
