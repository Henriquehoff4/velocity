using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Velocity.Engine.Core;
using Velocity.Engine.Net;
using Velocity.Engine.Persist;
using Velocity.Engine.Tools;

namespace Velocity.Engine;

public sealed class EngineError : Exception
{
    public string Code { get; }

    public EngineError(string code, string message) : base(message) => Code = code;
}

public sealed class Item
{
    public string Id = "";
    public string Kind = "http";
    public string Url = "";
    public List<string> Mirrors = new();
    public string? Authorization;
    public string? Cookie;
    public string? Referer;
    public string Folder = "";
    public string Name = "";
    public string PartPath = "";
    public string FinalPath = "";
    public string Display = "";
    public string Server = "";
    public string Status = "queued";
    public string ErrorCode = "";
    public string ErrorMessage = "";
    public string Note = "";
    public string Checksum = "";
    public string ChecksumState = "";
    public string Mode = "";
    public int Priority = 1;
    public long Order;
    public long Size = -1;
    public long Done;
    public string Etag = "";
    public string Modified = "";
    public double Average;
    public double Max;
    public bool Resumable = true;
    public bool Verified;
    public long AddedAt;
    public long FinishedAt;
    public long LimitBps;
    public int RetryCount;
    public long RetryAt;
    public string Intent = "";
    public IJob? Job;
    public CancellationTokenSource? Cts;
    public Task? Task;

    public bool IsLive => Job != null;
}

public sealed class Manager : IAsyncDisposable
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https" };
    private readonly object _gate = new();
    private readonly Store _store;
    private readonly Action<string, JsonNode?> _emit;
    private readonly HttpPool _pool = new();
    private readonly HostMemory _hosts = new();
    private readonly TokenBucket _global = new();
    private readonly Aria2Daemon _aria2 = new();
    private readonly List<Item> _items = new();
    private readonly Dictionary<string, string> _publicIps = new();
    private readonly Dictionary<string, (long Bytes, long At)> _osCounters = new();
    private readonly Dictionary<string, double> _osSpeeds = new();
    private List<NetLink> _links = new();
    private readonly Settings _settings = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private Task? _loop;
    private Task? _netLoop;
    private string _watchedId = "";
    private long _nextOrder = 1;
    private bool _dirty;
    private long _lastSave;
    private bool _disposed;

    public Manager(string dataDir, Action<string, JsonNode?> emit)
    {
        _store = new Store(dataDir);
        _emit = emit;
        Load();
    }

    public Settings CurrentSettings => _settings;

    private void Load()
    {
        _settings.Apply(_store.Read("settings.json"));
        if (_settings.Folder.Length == 0) _settings.Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (_store.Read("hosts.json") is JsonObject hosts)
            foreach (var pair in hosts)
                if (pair.Value is JsonObject entry)
                    _hosts.Set(pair.Key, new HostProfile { Best = (int)Json.Long(entry, "best"), Cap = (int)Json.Long(entry, "cap"), CapUntil = Json.Long(entry, "capUntil") });
        if (_store.Read("queue.json") is JsonArray queue)
        {
            foreach (var node in queue)
            {
                if (node is not JsonObject json) continue;
                var item = FromJson(json);
                if (item == null) continue;
                if (item.Status is "running") item.Status = _settings.AutoResume ? "queued" : "paused";
                _items.Add(item);
                _nextOrder = Math.Max(_nextOrder, item.Order + 1);
            }
        }
        _global.Rate = _settings.GlobalLimitBps;
    }

    public void Start()
    {
        _links = NetworkMonitor.Enumerate();
        _loop = Task.Run(() => LoopAsync(_stop.Token));
        _netLoop = Task.Run(() => NetLoopAsync(_stop.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var count = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _wake.WaitAsync(250, ct);
                while (_wake.CurrentCount > 0) await _wake.WaitAsync(ct);
                count++;
                Schedule();
                EmitTick(count % 4 == 0);
                if (!string.IsNullOrEmpty(_watchedId)) EmitMap();
                SaveIfDirty(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task NetLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
                var links = NetworkMonitor.Enumerate();
                lock (_gate)
                {
                    _links = ApplyPublicIpGroups(links);
                    var now = Environment.TickCount64;
                    foreach (var link in _links)
                    {
                        var bytes = NetworkMonitor.TryReceivedBytes(link.Id);
                        if (bytes < 0) continue;
                        if (_osCounters.TryGetValue(link.Id, out var previous) && now > previous.At)
                            _osSpeeds[link.Id] = Math.Max(0, bytes - previous.Bytes) * 1000.0 / (now - previous.At);
                        _osCounters[link.Id] = (bytes, now);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private List<NetLink> ApplyPublicIpGroups(List<NetLink> links)
    {
        if (_publicIps.Count == 0) return links;
        var byIp = new Dictionary<string, string>();
        var result = new List<NetLink>();
        foreach (var link in links)
        {
            if (_publicIps.TryGetValue(link.Id, out var ip) && ip.Length > 0)
            {
                if (byIp.TryGetValue(ip, out var group)) result.Add(link with { Group = group });
                else
                {
                    byIp[ip] = link.Group;
                    result.Add(link);
                }
            }
            else result.Add(link);
        }
        return result;
    }

    private IReadOnlyList<NetLink> LinksFor(SpeedProfile profile)
    {
        List<NetLink> links;
        lock (_gate) links = _links.ToList();
        var advanced = _settings.Advanced;
        var allowed = _settings.Mode == "advanced" ? advanced.AllowedLinks : Array.Empty<string>();
        var force = _settings.Mode == "advanced" && advanced.ForceSharedLinks;
        var candidates = allowed.Length > 0 ? links : links.Where(l => !l.Virtual || l.Kind == "test").ToList();
        if (candidates.Count == 0) return Array.Empty<NetLink>();
        var selected = NetworkMonitor.SelectForDownload(candidates, allowed, force);
        if (!profile.MultiLink && selected.Count > 1) selected = selected.Take(1).ToList();
        return selected;
    }

    private SpeedProfile ProfileFor(Item item)
    {
        var mode = item.Mode.Length > 0 ? item.Mode : _settings.Mode;
        return SpeedProfile.For(mode, _settings.Advanced);
    }

    private void Schedule()
    {
        List<Item> toStart = new();
        List<Item> toPreempt = new();
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var item in _items)
            {
                if (item.Status == "failed" && item.ErrorCode == "network" && item.RetryCount < 5 && item.RetryAt > 0 && item.RetryAt <= now)
                {
                    item.RetryCount++;
                    item.RetryAt = 0;
                    item.Status = "queued";
                    item.ErrorCode = "";
                    item.ErrorMessage = "";
                    MarkDirty();
                }
            }
            var ranked = _items.Where(i => i.Status is "queued" or "running").OrderBy(i => i.Priority).ThenBy(i => i.Order).ToList();
            var capacity = _settings.MaxConcurrent;
            var top = ranked.Take(capacity).ToHashSet();
            foreach (var item in ranked)
            {
                if (top.Contains(item))
                {
                    if (item.Status == "queued" && item.Job == null && item.Task == null) toStart.Add(item);
                }
                else if (item.Status == "running" && item.Job != null && string.IsNullOrEmpty(item.Intent))
                {
                    var waiting = top.FirstOrDefault(t => t.Status == "queued" && t.Priority < item.Priority);
                    if (waiting != null) toPreempt.Add(item);
                    else if (top.Count(t => t.Status == "running") + toStart.Count > capacity) toPreempt.Add(item);
                }
            }
            foreach (var item in toPreempt)
            {
                item.Intent = "queued";
                item.Cts?.Cancel();
            }
            var running = _items.Count(i => i.Status == "running" || i.Task != null);
            foreach (var item in toStart)
            {
                if (running >= capacity) break;
                StartLocked(item);
                running++;
            }
        }
    }

    private void StartLocked(Item item)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        item.Cts = cts;
        item.Intent = "";
        item.Status = "running";
        item.ErrorCode = "";
        item.ErrorMessage = "";
        item.Job = BuildJob(item);
        var job = item.Job;
        item.Task = Task.Run(async () =>
        {
            DownloadResult result;
            try
            {
                result = await job.RunAsync(cts.Token);
            }
            catch (Exception e)
            {
                result = new DownloadResult(DownloadEnd.Failed, "internal", $"Erro interno: {e.GetType().Name}");
            }
            OnEnded(item, job, result);
        });
        MarkDirty();
        EmitItem(item);
    }

    private IJob BuildJob(Item item)
    {
        if (item.Kind == "torrent") return new Aria2Job(_aria2, item.Url, item.Folder);
        var profile = ProfileFor(item);
        if (item.Kind == "video") return new YtDlpJob(item.Url, item.Folder, profile.Mode);
        var auth = item.Authorization != null || item.Cookie != null || item.Referer != null
            ? new RequestAuth { Authorization = item.Authorization, Cookie = item.Cookie, Referer = item.Referer }
            : null;
        var sources = new List<SourceSpec>();
        foreach (var url in new[] { item.Url }.Concat(item.Mirrors))
        {
            var parsed = new Uri(url);
            var (clean, withUser) = HttpProtocol.SplitUserInfo(parsed, auth);
            sources.Add(new SourceSpec(clean, withUser));
        }
        var download = new HttpDownload(new DownloadSpec
        {
            Id = item.Id,
            Sources = sources,
            Folder = item.Folder,
            PartPath = item.PartPath,
            FileName = item.Name.Length > 0 && item.Size != -2 ? item.Name : null,
            Checksum = item.Checksum.Length > 0 ? item.Checksum : null,
            ExpectedSize = item.Size,
            ExpectedEtag = item.Etag,
            ExpectedModified = item.Modified,
            Profile = profile with { LimitBps = item.LimitBps > 0 ? item.LimitBps : profile.LimitBps },
            GlobalLimit = _global,
            Pool = _pool,
            Hosts = _hosts,
            Links = () => LinksFor(profile),
            OnProbed = probe =>
            {
                lock (_gate)
                {
                    item.Size = probe.Size;
                    item.Etag = probe.Etag;
                    item.Modified = probe.LastModified;
                    item.Server = probe.Server;
                    item.Resumable = probe.Ranges;
                    if (!item.NameLocked()) item.Name = probe.FileName;
                    MarkDirty();
                }
            },
        });
        return new HttpJob(download);
    }

    private void OnEnded(Item item, IJob job, DownloadResult result)
    {
        Item snapshot;
        var removed = false;
        lock (_gate)
        {
            var stats = job.Stats;
            item.Done = Math.Max(stats.Done, result.End == DownloadEnd.Done ? result.Size : 0);
            item.Average = Math.Max(item.Average, stats.Average);
            item.Max = Math.Max(item.Max, stats.Max);
            if (job is Aria2Job { Name: { Length: > 0 } torrentName }) item.Name = torrentName;
            if (job is YtDlpJob { Name: { Length: > 0 } videoName }) item.Name = videoName;
            if (stats.Note.Length > 0) item.Note = stats.Note;
            item.Job = null;
            item.Cts?.Dispose();
            item.Cts = null;
            item.Task = null;
            var intent = item.Intent;
            item.Intent = "";
            switch (result.End)
            {
                case DownloadEnd.Done:
                    item.Status = "done";
                    item.FinalPath = result.FinalPath;
                    item.Size = result.Size;
                    item.Verified = result.Verified;
                    item.ChecksumState = result.ChecksumState;
                    item.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    item.Average = result.AverageBps > 0 ? result.AverageBps : item.Average;
                    item.Max = Math.Max(item.Max, result.MaxBps);
                    item.Name = Path.GetFileName(result.FinalPath);
                    if (result.BestConnections > 0 && item.Server.Length > 0) _hosts.RecordBest(item.Server, result.BestConnections);
                    AppendHistory(item, "done");
                    break;
                case DownloadEnd.Paused:
                    if (intent == "cancel")
                    {
                        DeletePartial(item);
                        _items.Remove(item);
                        AppendHistory(item, "cancelled");
                        removed = true;
                    }
                    else if (_stop.IsCancellationRequested)
                    {
                        item.Status = "queued";
                    }
                    else
                    {
                        item.Status = intent == "queued" ? "queued" : "paused";
                    }
                    break;
                case DownloadEnd.NeedsLink:
                    item.Status = "needs-link";
                    item.ErrorCode = result.Code;
                    item.ErrorMessage = result.Message;
                    break;
                case DownloadEnd.NeedsAuth:
                    item.Status = "needs-auth";
                    item.ErrorCode = result.Code;
                    item.ErrorMessage = result.Message;
                    break;
                default:
                    if (intent == "cancel")
                    {
                        DeletePartial(item);
                        _items.Remove(item);
                        AppendHistory(item, "cancelled");
                        removed = true;
                        break;
                    }
                    item.Status = "failed";
                    item.ErrorCode = result.Code;
                    item.ErrorMessage = result.Message;
                    if (result.Code == "network" && item.RetryCount < 5) item.RetryAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30000L * (item.RetryCount + 1);
                    AppendHistory(item, "failed");
                    break;
            }
            MarkDirty();
            snapshot = item;
        }
        if (removed) _emit("item.removed", new JsonObject { ["id"] = item.Id });
        else EmitItem(snapshot);
        SaveIfDirty(true);
    }

    private void AppendHistory(Item item, string outcome)
    {
        try
        {
            _store.AppendHistory(new JsonObject
            {
                ["id"] = item.Id,
                ["name"] = item.Name,
                ["kind"] = item.Kind,
                ["server"] = item.Server,
                ["size"] = item.Size,
                ["path"] = item.FinalPath,
                ["outcome"] = outcome,
                ["error"] = item.ErrorMessage,
                ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["average"] = item.Average,
                ["max"] = item.Max,
                ["verified"] = item.Verified,
                ["checksum"] = item.ChecksumState,
            });
        }
        catch (IOException)
        {
        }
    }

    private static void DeletePartial(Item item)
    {
        if (item.PartPath.Length == 0) return;
        foreach (var path in new[] { item.PartPath, item.PartPath + ".vdl", item.PartPath + ".vdl.tmp" })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void MarkDirty() => _dirty = true;

    private void SaveIfDirty(bool force)
    {
        JsonArray queue;
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (!_dirty || (!force && now - _lastSave < 2000)) return;
            _dirty = false;
            _lastSave = now;
            queue = new JsonArray(_items.Select(i => (JsonNode)ToStored(i)).ToArray());
        }
        try
        {
            _store.Write("queue.json", queue);
            var hosts = new JsonObject();
            foreach (var pair in _hosts.All)
                hosts[pair.Key] = new JsonObject { ["best"] = pair.Value.Best, ["cap"] = pair.Value.Cap, ["capUntil"] = pair.Value.CapUntil };
            _store.Write("hosts.json", hosts);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lock (_gate) _dirty = true;
        }
    }

    private static JsonObject ToStored(Item item)
    {
        var secret = new JsonObject
        {
            ["url"] = item.Url,
            ["mirrors"] = new JsonArray(item.Mirrors.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["authorization"] = item.Authorization,
            ["cookie"] = item.Cookie,
            ["referer"] = item.Referer,
        };
        return new JsonObject
        {
            ["id"] = item.Id,
            ["kind"] = item.Kind,
            ["secret"] = Secrets.Protect(secret.ToJsonString()),
            ["folder"] = item.Folder,
            ["name"] = item.Name,
            ["partPath"] = item.PartPath,
            ["finalPath"] = item.FinalPath,
            ["display"] = item.Display,
            ["server"] = item.Server,
            ["status"] = item.Status,
            ["errorCode"] = item.ErrorCode,
            ["errorMessage"] = item.ErrorMessage,
            ["note"] = item.Note,
            ["checksum"] = item.Checksum,
            ["checksumState"] = item.ChecksumState,
            ["mode"] = item.Mode,
            ["priority"] = item.Priority,
            ["order"] = item.Order,
            ["size"] = item.Size,
            ["done"] = item.Done,
            ["etag"] = item.Etag,
            ["modified"] = item.Modified,
            ["average"] = item.Average,
            ["max"] = item.Max,
            ["resumable"] = item.Resumable,
            ["verified"] = item.Verified,
            ["addedAt"] = item.AddedAt,
            ["finishedAt"] = item.FinishedAt,
            ["limitBps"] = item.LimitBps,
        };
    }

    private static Item? FromJson(JsonObject json)
    {
        var plain = Secrets.Unprotect(Json.Str(json, "secret"));
        if (plain == null) return null;
        JsonObject? secret;
        try
        {
            secret = JsonNode.Parse(plain) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (secret == null) return null;
        var item = new Item
        {
            Id = Json.Str(json, "id"),
            Kind = Json.Str(json, "kind", "http"),
            Url = Json.Str(secret, "url"),
            Mirrors = (secret["mirrors"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(m => m.Length > 0).ToList() ?? new(),
            Authorization = secret["authorization"]?.GetValue<string>(),
            Cookie = secret["cookie"]?.GetValue<string>(),
            Referer = secret["referer"]?.GetValue<string>(),
            Folder = Json.Str(json, "folder"),
            Name = Json.Str(json, "name"),
            PartPath = Json.Str(json, "partPath"),
            FinalPath = Json.Str(json, "finalPath"),
            Display = Json.Str(json, "display"),
            Server = Json.Str(json, "server"),
            Status = Json.Str(json, "status", "queued"),
            ErrorCode = Json.Str(json, "errorCode"),
            ErrorMessage = Json.Str(json, "errorMessage"),
            Note = Json.Str(json, "note"),
            Checksum = Json.Str(json, "checksum"),
            ChecksumState = Json.Str(json, "checksumState"),
            Mode = Json.Str(json, "mode"),
            Priority = (int)Json.Long(json, "priority", 1),
            Order = Json.Long(json, "order"),
            Size = Json.Long(json, "size", -1),
            Done = Json.Long(json, "done"),
            Etag = Json.Str(json, "etag"),
            Modified = Json.Str(json, "modified"),
            Average = Json.Dbl(json, "average"),
            Max = Json.Dbl(json, "max"),
            Resumable = Json.Bool(json, "resumable", true),
            Verified = Json.Bool(json, "verified"),
            AddedAt = Json.Long(json, "addedAt"),
            FinishedAt = Json.Long(json, "finishedAt"),
            LimitBps = Json.Long(json, "limitBps"),
        };
        return item.Id.Length > 0 && item.Url.Length > 0 ? item : null;
    }

    public static string DisplayOf(Uri url) => $"{url.Host}{url.AbsolutePath}";

    public static (string Kind, Uri? Url) Classify(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return ("torrent", null);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || !AllowedSchemes.Contains(url.Scheme) || string.IsNullOrEmpty(url.Host))
            throw new EngineError("bad-url", "Esse link não é um endereço http, https ou magnet válido.");
        if (url.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) return ("torrent", url);
        return ("http", url);
    }

    public async Task<JsonNode> InspectAsync(JsonObject? args, CancellationToken ct)
    {
        var urls = (args?["urls"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(u => u.Length > 0).Take(50).ToArray() ?? Array.Empty<string>();
        var results = new JsonObject[urls.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, urls.Length), new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (index, token) =>
        {
            results[index] = await InspectOneAsync(urls[index], token);
        });
        return new JsonArray(results.Select(r => (JsonNode)r).ToArray());
    }

    private async Task<JsonObject> InspectOneAsync(string raw, CancellationToken ct)
    {
        var result = new JsonObject { ["input"] = Redact(raw) };
        try
        {
            var (kind, url) = Classify(raw);
            result["kind"] = kind;
            if (kind == "torrent")
            {
                result["ok"] = true;
                result["name"] = url != null ? FileNames.FromResponse(null, url, "") : MagnetName(raw);
                result["server"] = url?.Host ?? "Rede BitTorrent";
                result["size"] = -1;
                result["toolReady"] = ToolFinder.Find("aria2c") != null;
                return result;
            }
            var (clean, auth) = HttpProtocol.SplitUserInfo(url!, null);
            var probe = await HttpProtocol.ProbeAsync(_pool.Get(null), clean, auth, ct);
            result["ok"] = true;
            result["name"] = probe.FileName;
            result["size"] = probe.Size;
            result["server"] = probe.Server;
            result["ranges"] = probe.Ranges;
            result["html"] = probe.IsHtml;
            result["contentType"] = probe.ContentType;
            result["digest"] = probe.Digest.Length > 0;
            result["videoReady"] = ToolFinder.Find("yt-dlp") != null;
        }
        catch (HttpFailure failure)
        {
            result["ok"] = false;
            result["code"] = failure.Code;
            result["message"] = failure.Message;
        }
        catch (HttpRequestException)
        {
            result["ok"] = false;
            result["code"] = "network";
            result["message"] = "Não foi possível conectar ao servidor. Confira o endereço e a conexão.";
        }
        catch (TaskCanceledException)
        {
            result["ok"] = false;
            result["code"] = "timeout";
            result["message"] = "O servidor demorou demais para responder.";
        }
        catch (EngineError error)
        {
            result["ok"] = false;
            result["code"] = error.Code;
            result["message"] = error.Message;
        }
        return result;
    }

    private static string MagnetName(string magnet)
    {
        var at = magnet.IndexOf("dn=", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "Torrent";
        var end = magnet.IndexOf('&', at);
        var value = end < 0 ? magnet[(at + 3)..] : magnet[(at + 3)..end];
        try
        {
            return FileNames.Sanitize(Uri.UnescapeDataString(value.Replace('+', ' ')));
        }
        catch (UriFormatException)
        {
            return "Torrent";
        }
    }

    public static string Redact(string raw)
    {
        if (raw.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return "magnet";
        return Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url) ? DisplayOf(url) : "";
    }

    public JsonNode Add(JsonObject? args)
    {
        var urls = (args?["urls"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(u => u.Length is > 0 and <= 16384).Take(500).ToList() ?? new();
        var asMirrors = Json.Bool(args, "mirrors");
        var kindOverride = Json.Str(args, "kind");
        var folder = Json.Str(args, "folder");
        var priority = Math.Clamp((int)Json.Long(args, "priority", 1), 0, 2);
        var checksum = NormalizeChecksum(Json.Str(args, "checksum"));
        var torrentFile = Json.Str(args, "torrentFile");
        if (urls.Count == 0 && torrentFile.Length == 0) throw new EngineError("no-links", "Cole pelo menos um link.");
        if (folder.Length == 0) folder = _settings.Folder;
        if (!Path.IsPathFullyQualified(folder)) throw new EngineError("bad-folder", "Escolha uma pasta de destino válida.");
        var authorization = Json.Str(args, "authorization");
        var cookie = Json.Str(args, "cookie");
        var referer = Json.Str(args, "referer");
        var user = Json.Str(args, "user");
        var password = Json.Str(args, "password");
        if (user.Length > 0) authorization = BasicAuth(user, password);
        var added = new List<Item>();
        lock (_gate)
        {
            if (torrentFile.Length > 0)
            {
                if (!Path.IsPathFullyQualified(torrentFile) || !torrentFile.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                    throw new EngineError("bad-file", "Escolha um arquivo .torrent.");
                var item = NewItem("torrent", "file:" + torrentFile, folder, priority);
                item.Name = Path.GetFileNameWithoutExtension(torrentFile);
                item.Display = "Arquivo .torrent";
                item.Server = "Rede BitTorrent";
                added.Add(item);
            }
            if (asMirrors && urls.Count > 1)
            {
                var valid = urls.Select(u => Classify(u)).ToList();
                if (valid.Any(v => v.Kind != "http")) throw new EngineError("bad-mirrors", "Mirrors precisam ser links http ou https do mesmo arquivo.");
                var item = NewItem("http", urls[0], folder, priority);
                item.Mirrors = urls.Skip(1).ToList();
                FillHttp(item, valid[0].Url!, checksum, authorization, cookie, referer);
                added.Add(item);
            }
            else
            {
                foreach (var raw in urls)
                {
                    var (kind, url) = Classify(raw);
                    if (kindOverride == "video" && url != null) kind = "video";
                    var item = NewItem(kind, raw.Trim(), folder, priority);
                    if (url != null && kind != "torrent") FillHttp(item, url, checksum, authorization, cookie, referer);
                    else
                    {
                        item.Name = url != null ? FileNames.FromResponse(null, url, "") : MagnetName(raw);
                        item.Display = url != null ? DisplayOf(url) : "Link magnet";
                        item.Server = url?.Host ?? "Rede BitTorrent";
                    }
                    if (kind == "video")
                    {
                        item.Name = url!.Host;
                        item.Resumable = true;
                        item.Size = -1;
                    }
                    added.Add(item);
                }
            }
            _items.AddRange(added);
            MarkDirty();
        }
        SaveIfDirty(true);
        foreach (var item in added) EmitItem(item);
        Wake();
        return new JsonArray(added.Select(i => (JsonNode)ToUi(i)).ToArray());
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private void FillHttp(Item item, Uri url, string checksum, string authorization, string cookie, string referer)
    {
        item.Display = DisplayOf(url);
        item.Server = url.Host;
        item.Name = FileNames.FromResponse(null, url, "");
        item.Checksum = checksum;
        item.Authorization = authorization.Length > 0 ? authorization : null;
        item.Cookie = cookie.Length > 0 ? cookie : null;
        item.Referer = referer.Length > 0 ? referer : null;
    }

    private static string BasicAuth(string user, string password) =>
        "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));

    private Item NewItem(string kind, string url, string folder, int priority)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        return new Item
        {
            Id = id,
            Kind = kind,
            Url = url,
            Folder = folder,
            Priority = priority,
            Order = _nextOrder++,
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PartPath = Path.Combine(folder, $"velocity-{id}.vpart"),
        };
    }

    public static string NormalizeChecksum(string raw)
    {
        var text = raw.Trim().ToLowerInvariant();
        if (text.Length == 0) return "";
        if (text.Contains(':')) return text;
        return text.Length switch
        {
            32 => "md5:" + text,
            40 => "sha1:" + text,
            64 => "sha256:" + text,
            128 => "sha512:" + text,
            _ => throw new EngineError("bad-checksum", "Checksum inválido. Use um hash MD5, SHA-1, SHA-256 ou SHA-512."),
        };
    }

    private Item Require(string id)
    {
        foreach (var item in _items)
            if (item.Id == id) return item;
        throw new EngineError("not-found", "Esse download não existe mais.");
    }

    public void Pause(string id)
    {
        lock (_gate)
        {
            var item = Require(id);
            if (item.Job != null)
            {
                item.Intent = "paused";
                item.Cts?.Cancel();
            }
            else if (item.Status is "queued" or "failed")
            {
                item.Status = "paused";
                EmitItem(item);
                MarkDirty();
            }
        }
    }

    public void Resume(string id)
    {
        lock (_gate)
        {
            var item = Require(id);
            if (item.Job != null)
            {
                if (item.Intent == "paused") item.Intent = "queued";
                return;
            }
            if (item.Status is "paused" or "failed" or "needs-link" or "needs-auth")
            {
                item.Status = "queued";
                item.RetryCount = 0;
                item.ErrorCode = "";
                item.ErrorMessage = "";
                EmitItem(item);
                Wake();
                MarkDirty();
            }
        }
    }

    public void PauseAll()
    {
        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (item.Job != null)
                {
                    item.Intent = "paused";
                    item.Cts?.Cancel();
                }
                else if (item.Status == "queued")
                {
                    item.Status = "paused";
                    EmitItem(item);
                }
            }
            MarkDirty();
        }
    }

    public void ResumeAll()
    {
        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (item.Status != "paused" || item.Job != null) continue;
                item.Status = "queued";
                EmitItem(item);
            }
            MarkDirty();
        }
    }

    public void Cancel(string id)
    {
        lock (_gate)
        {
            var item = Require(id);
            if (item.Job != null)
            {
                item.Intent = "cancel";
                item.Cts?.Cancel();
                return;
            }
            DeletePartial(item);
            _items.Remove(item);
            AppendHistory(item, "cancelled");
            MarkDirty();
        }
        _emit("item.removed", new JsonObject { ["id"] = id });
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var item = Require(id);
            if (item.Job != null || item.Status != "done") throw new EngineError("busy", "Só downloads concluídos saem da lista sem cancelar.");
            _items.Remove(item);
            MarkDirty();
        }
        _emit("item.removed", new JsonObject { ["id"] = id });
    }

    public void ClearDone()
    {
        List<string> ids;
        lock (_gate)
        {
            ids = _items.Where(i => i.Status == "done").Select(i => i.Id).ToList();
            _items.RemoveAll(i => i.Status == "done");
            MarkDirty();
        }
        foreach (var id in ids) _emit("item.removed", new JsonObject { ["id"] = id });
    }

    public void SetPriority(string id, int priority)
    {
        lock (_gate)
        {
            var item = Require(id);
            item.Priority = Math.Clamp(priority, 0, 2);
            MarkDirty();
            EmitItem(item);
        }
    }

    public void Move(string id, string direction)
    {
        lock (_gate)
        {
            var item = Require(id);
            var peers = _items.Where(i => i.Status != "done" && i.Priority == item.Priority).OrderBy(i => i.Order).ToList();
            var index = peers.IndexOf(item);
            var target = direction switch
            {
                "up" => index - 1,
                "down" => index + 1,
                "top" => 0,
                _ => peers.Count - 1,
            };
            if (index < 0 || target < 0 || target >= peers.Count || target == index) return;
            peers.RemoveAt(index);
            peers.Insert(target, item);
            var orders = peers.Select(p => p.Order).OrderBy(o => o).ToList();
            for (var i = 0; i < peers.Count; i++) peers[i].Order = orders[i];
            MarkDirty();
            foreach (var peer in peers) EmitItem(peer);
        }
    }

    public void Reorder(JsonObject? args)
    {
        var ids = (args?["ids"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToList() ?? new();
        lock (_gate)
        {
            var peers = ids.Select(id => _items.FirstOrDefault(i => i.Id == id)).Where(i => i != null).Select(i => i!).ToList();
            var orders = peers.Select(p => p.Order).OrderBy(o => o).ToList();
            for (var i = 0; i < peers.Count; i++) peers[i].Order = orders[i];
            MarkDirty();
            foreach (var peer in peers) EmitItem(peer);
        }
    }

    public async Task UpdateLinkAsync(JsonObject? args, CancellationToken ct)
    {
        var id = Json.Str(args, "id");
        var raw = Json.Str(args, "url").Trim();
        var (kind, url) = Classify(raw);
        if (kind != "http") throw new EngineError("bad-url", "Use um link http ou https.");
        Item item;
        lock (_gate) item = Require(id);
        var (clean, auth) = HttpProtocol.SplitUserInfo(url!, item.Authorization != null ? new RequestAuth { Authorization = item.Authorization, Cookie = item.Cookie } : null);
        var probe = await HttpProtocol.ProbeAsync(_pool.Get(null), clean, auth, ct);
        lock (_gate)
        {
            if (item.Size > 0 && probe.Size != item.Size)
                throw new EngineError("link-mismatch", $"Esse link é de outro arquivo: tem {Format.Bytes(Math.Max(probe.Size, 0))}, e o download tem {Format.Bytes(item.Size)}.");
            item.Url = raw;
            item.Server = probe.Server;
            item.Display = DisplayOf(clean);
            if (item.Job == null)
            {
                item.Status = "queued";
                item.ErrorCode = "";
                item.ErrorMessage = "";
            }
            MarkDirty();
            EmitItem(item);
        }
    }

    public void SetAuth(JsonObject? args)
    {
        lock (_gate)
        {
            var item = Require(Json.Str(args, "id"));
            item.Authorization = BasicAuth(Json.Str(args, "user"), Json.Str(args, "password"));
            if (item.Job == null)
            {
                item.Status = "queued";
                item.ErrorCode = "";
                item.ErrorMessage = "";
            }
            MarkDirty();
            EmitItem(item);
        }
    }

    public JsonNode ApplySettings(JsonObject? patch)
    {
        JsonObject result;
        lock (_gate)
        {
            _settings.Apply(patch);
            _global.Rate = _settings.GlobalLimitBps;
            _store.Write("settings.json", _settings.ToJson());
            result = _settings.ToJson();
        }
        return result;
    }

    public JsonArray ListItems()
    {
        lock (_gate) return new JsonArray(Ordered().Select(i => (JsonNode)ToUi(i)).ToArray());
    }

    private IEnumerable<Item> Ordered() => _items.OrderBy(i => i.Status == "done" ? 1 : 0).ThenBy(i => i.Priority).ThenBy(i => i.Order);

    public void Watch(string id) => _watchedId = id;

    private JsonObject ToUi(Item item)
    {
        var stats = item.Job?.Stats;
        var size = stats is { Size: > 0 } ? stats.Size : item.Size;
        var done = stats != null && stats.Phase != "connecting" ? stats.Done : item.Done;
        if (item.Job != null) item.Done = done;
        var ui = new JsonObject
        {
            ["id"] = item.Id,
            ["kind"] = item.Kind,
            ["name"] = stats is { Note.Length: > 0 } && item.Kind == "torrent" ? stats.Note : item.Name,
            ["display"] = item.Display,
            ["server"] = item.Server,
            ["status"] = item.Status,
            ["phase"] = stats?.Phase ?? "",
            ["priority"] = item.Priority,
            ["order"] = item.Order,
            ["size"] = size,
            ["done"] = done,
            ["speed"] = stats?.Speed ?? 0,
            ["average"] = stats is { Average: > 0 } ? stats.Average : item.Average,
            ["max"] = stats != null ? Math.Max(stats.Max, item.Max) : item.Max,
            ["eta"] = stats?.EtaSeconds ?? -1,
            ["connections"] = stats?.Connections ?? 0,
            ["target"] = stats?.Target ?? 0,
            ["fragments"] = stats?.Fragments ?? 0,
            ["bottleneck"] = stats?.Bottleneck ?? "",
            ["note"] = stats is { Note.Length: > 0 } && item.Kind != "torrent" ? stats.Note : item.Note,
            ["resumable"] = stats?.Resumable ?? item.Resumable,
            ["verified"] = item.Verified,
            ["checksum"] = item.Checksum.Length > 0,
            ["checksumState"] = item.ChecksumState,
            ["verifyDone"] = stats?.VerifyDone ?? 0,
            ["errorCode"] = item.ErrorCode,
            ["errorMessage"] = item.ErrorMessage,
            ["folder"] = item.Folder,
            ["finalPath"] = item.FinalPath,
            ["mirrors"] = item.Mirrors.Count,
            ["mode"] = item.Mode.Length > 0 ? item.Mode : _settings.Mode,
            ["addedAt"] = item.AddedAt,
            ["finishedAt"] = item.FinishedAt,
        };
        if (stats != null)
            ui["links"] = new JsonArray(stats.Links.Select(l => (JsonNode)new JsonObject { ["id"] = l.Id, ["name"] = l.Name, ["speed"] = l.Speed, ["connections"] = l.Connections, ["noGain"] = l.NoGain }).ToArray());
        return ui;
    }

    private void EmitItem(Item item)
    {
        JsonObject data;
        lock (_gate) data = ToUi(item);
        _emit("item", data);
    }

    private void EmitTick(bool withLinks)
    {
        var data = new JsonObject();
        lock (_gate)
        {
            var live = _items.Where(i => i.Job != null).ToList();
            data["items"] = new JsonArray(live.Select(i => (JsonNode)ToUi(i)).ToArray());
            data["total"] = live.Sum(i => i.Job?.Stats.Speed ?? 0);
            if (withLinks) data["net"] = NetSummaryLocked(live);
        }
        _emit("tick", data);
    }

    private JsonObject NetSummaryLocked(List<Item> live)
    {
        var perLink = new Dictionary<string, (double Speed, int Connections, bool NoGain)>();
        foreach (var item in live)
        {
            var stats = item.Job?.Stats;
            if (stats == null) continue;
            foreach (var link in stats.Links)
            {
                perLink.TryGetValue(link.Id, out var value);
                perLink[link.Id] = (value.Speed + link.Speed, value.Connections + link.Connections, value.NoGain || link.NoGain);
            }
        }
        var links = new JsonArray();
        var groupCounts = _links.Where(l => !l.Virtual || l.Kind == "test").GroupBy(l => l.Group).ToDictionary(g => g.Key, g => g.Count());
        foreach (var link in _links)
        {
            perLink.TryGetValue(link.Id, out var value);
            links.Add((JsonNode)new JsonObject
            {
                ["id"] = link.Id,
                ["name"] = link.Name,
                ["kind"] = link.Kind,
                ["ipv4"] = link.LocalV4?.ToString() ?? "",
                ["ipv6"] = link.LocalV6?.ToString() ?? "",
                ["gateway"] = link.Kind == "test" ? "" : link.Gateway,
                ["group"] = link.Group,
                ["sharedGroup"] = groupCounts.GetValueOrDefault(link.Group) > 1,
                ["nominal"] = link.NominalBps,
                ["speed"] = value.Speed,
                ["osSpeed"] = _osSpeeds.GetValueOrDefault(link.Id),
                ["connections"] = value.Connections,
                ["noGain"] = value.NoGain,
                ["virtual"] = link.Virtual,
                ["publicIp"] = _publicIps.GetValueOrDefault(link.Id, ""),
            });
        }
        var physical = _links.Where(l => !l.Virtual || l.Kind == "test").ToList();
        return new JsonObject
        {
            ["links"] = links,
            ["independent"] = physical.Select(l => l.Group).Distinct().Count(),
            ["combined"] = perLink.Values.Sum(v => v.Speed),
        };
    }

    public JsonNode NetSummary()
    {
        lock (_gate) return NetSummaryLocked(_items.Where(i => i.Job != null).ToList());
    }

    public async Task<JsonNode> CheckPublicAsync(CancellationToken ct)
    {
        List<NetLink> links;
        lock (_gate) links = _links.ToList();
        var results = new JsonArray();
        foreach (var link in links.Where(l => l.Kind != "test"))
        {
            var ip = "";
            var error = "";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(8000);
                using var request = HttpProtocol.Build(HttpMethod.Get, new Uri("https://api.ipify.org/"), null, false);
                using var response = await _pool.Get(link).SendAsync(request, timeout.Token);
                var text = (await response.Content.ReadAsStringAsync(timeout.Token)).Trim();
                if (System.Net.IPAddress.TryParse(text, out _)) ip = text;
                else error = "Resposta inválida do serviço.";
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
            {
                error = "A rede não respondeu.";
            }
            lock (_gate)
            {
                if (ip.Length > 0) _publicIps[link.Id] = ip;
                else _publicIps.Remove(link.Id);
            }
            results.Add((JsonNode)new JsonObject { ["id"] = link.Id, ["name"] = link.Name, ["ip"] = ip, ["error"] = error });
        }
        lock (_gate) _links = ApplyPublicIpGroups(NetworkMonitor.AssignGroups(_links));
        return results;
    }

    private void EmitMap()
    {
        MapSnapshot? map = null;
        var id = _watchedId;
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            map = item?.Job?.Snapshot();
        }
        if (map == null) return;
        var done = new JsonArray();
        foreach (var (start, end) in map.Done) done.Add((JsonNode)new JsonArray(start, end));
        var active = new JsonArray();
        foreach (var span in map.Active)
            active.Add((JsonNode)new JsonObject { ["s"] = span.Start, ["p"] = span.Pos, ["e"] = span.End, ["l"] = span.LinkId, ["v"] = span.Speed, ["w"] = span.WorkerId });
        _emit("map", new JsonObject { ["id"] = id, ["size"] = map.Size, ["done"] = done, ["active"] = active });
    }

    public JsonNode History(int limit) => _store.ReadHistory(Math.Clamp(limit, 1, 2000));
    public void ClearHistory() => _store.ClearHistory();

    public JsonNode Tools()
    {
        JsonObject Describe(string name, string versionArg)
        {
            var path = ToolFinder.Find(name);
            var version = path != null ? ToolFinder.Version(path, versionArg) : null;
            return new JsonObject { ["name"] = name, ["available"] = path != null, ["version"] = version ?? "", ["bundled"] = path != null && ToolFinder.ToolsDir != null && path.StartsWith(ToolFinder.ToolsDir, StringComparison.OrdinalIgnoreCase) };
        }

        return new JsonArray(Describe("aria2c", "--version"), Describe("yt-dlp", "--version"), Describe("ffmpeg", "-version"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        List<Task> tasks;
        lock (_gate)
        {
            tasks = _items.Select(i => i.Task).Where(t => t != null).Select(t => t!).ToList();
        }
        _stop.Cancel();
        try
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(4000));
        }
        catch (OperationCanceledException)
        {
        }
        SaveIfDirty(true);
        _aria2.Dispose();
        _pool.Dispose();
    }
}

internal static class ItemExtensions
{
    public static bool NameLocked(this Item item) => item.Status == "done";
}
