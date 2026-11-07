using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Velocity.Engine.Core;

namespace Velocity.Engine.Persist;

public sealed class Settings
{
    public string Mode = "auto";
    public string Folder = "";
    public int MaxConcurrent = 3;
    public long GlobalLimitBps;
    public bool AutoResume = true;
    public bool Notifications = true;
    public AdvancedOptions Advanced = new();

    public JsonObject ToJson() => new()
    {
        ["mode"] = Mode,
        ["folder"] = Folder,
        ["maxConcurrent"] = MaxConcurrent,
        ["globalLimitBps"] = GlobalLimitBps,
        ["autoResume"] = AutoResume,
        ["notifications"] = Notifications,
        ["advanced"] = new JsonObject
        {
            ["maxConnections"] = Advanced.MaxConnections,
            ["minFragmentKiB"] = Advanced.MinFragmentKiB,
            ["bufferKiB"] = Advanced.BufferKiB,
            ["limitBps"] = Advanced.LimitBps,
            ["ecoLimitBps"] = Advanced.EcoLimitBps,
            ["adaptive"] = Advanced.Adaptive,
            ["multiLink"] = Advanced.MultiLink,
            ["verify"] = Advanced.Verify,
            ["forceSharedLinks"] = Advanced.ForceSharedLinks,
            ["allowedLinks"] = new JsonArray(Advanced.AllowedLinks.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
        },
    };

    public void Apply(JsonNode? patch)
    {
        if (patch is not JsonObject json) return;
        var mode = Json.Str(json, "mode");
        if (mode is "auto" or "turbo" or "eco" or "advanced") Mode = mode;
        var folder = Json.Str(json, "folder");
        if (folder.Length > 0 && Path.IsPathFullyQualified(folder)) Folder = folder;
        if (json.ContainsKey("maxConcurrent")) MaxConcurrent = Math.Clamp((int)Json.Long(json, "maxConcurrent", 3), 1, 16);
        if (json.ContainsKey("globalLimitBps")) GlobalLimitBps = Math.Max(0, Json.Long(json, "globalLimitBps"));
        if (json.ContainsKey("autoResume")) AutoResume = Json.Bool(json, "autoResume", true);
        if (json.ContainsKey("notifications")) Notifications = Json.Bool(json, "notifications", true);
        if (json["advanced"] is JsonObject a)
        {
            var current = Advanced;
            Advanced = current with
            {
                MaxConnections = a.ContainsKey("maxConnections") ? Math.Clamp((int)Json.Long(a, "maxConnections", 16), 1, 64) : current.MaxConnections,
                MinFragmentKiB = a.ContainsKey("minFragmentKiB") ? Math.Clamp((int)Json.Long(a, "minFragmentKiB", 1024), 64, 1024 * 1024) : current.MinFragmentKiB,
                BufferKiB = a.ContainsKey("bufferKiB") ? Math.Clamp((int)Json.Long(a, "bufferKiB", 256), 64, 4096) : current.BufferKiB,
                LimitBps = a.ContainsKey("limitBps") ? Math.Max(0, Json.Long(a, "limitBps")) : current.LimitBps,
                EcoLimitBps = a.ContainsKey("ecoLimitBps") ? Math.Max(64 * 1024, Json.Long(a, "ecoLimitBps", current.EcoLimitBps)) : current.EcoLimitBps,
                Adaptive = a.ContainsKey("adaptive") ? Json.Bool(a, "adaptive", true) : current.Adaptive,
                MultiLink = a.ContainsKey("multiLink") ? Json.Bool(a, "multiLink", true) : current.MultiLink,
                Verify = a.ContainsKey("verify") ? Json.Bool(a, "verify", true) : current.Verify,
                ForceSharedLinks = a.ContainsKey("forceSharedLinks") ? Json.Bool(a, "forceSharedLinks") : current.ForceSharedLinks,
                AllowedLinks = a["allowedLinks"] is JsonArray list ? list.Select(n => n?.GetValue<string>() ?? "").Where(v => v.Length > 0).Take(32).ToArray() : current.AllowedLinks,
            };
        }
    }
}

public static class Json
{
    public static string Str(JsonObject? json, string key, string fallback = "")
    {
        if (json?[key] is not JsonValue value) return fallback;
        return value.TryGetValue<string>(out var text) ? text : fallback;
    }

    public static long Long(JsonObject? json, string key, long fallback = 0)
    {
        if (json?[key] is not JsonValue value) return fallback;
        if (value.TryGetValue<long>(out var number)) return number;
        if (value.TryGetValue<int>(out var small)) return small;
        if (value.TryGetValue<double>(out var real)) return (long)real;
        return fallback;
    }

    public static double Dbl(JsonObject? json, string key, double fallback = 0)
    {
        if (json?[key] is not JsonValue value) return fallback;
        return value.TryGetValue<double>(out var real) ? real : fallback;
    }

    public static bool Bool(JsonObject? json, string key, bool fallback = false)
    {
        if (json?[key] is not JsonValue value) return fallback;
        return value.TryGetValue<bool>(out var flag) ? flag : fallback;
    }
}

public static class Secrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("velocity.secrets.v1");

    public static string Protect(string plain)
    {
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string? Unprotect(string stored)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

public sealed class Store
{
    private readonly string _folder;
    private readonly object _gate = new();

    public Store(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    private string PathOf(string name) => Path.Combine(_folder, name);

    public JsonNode? Read(string name)
    {
        lock (_gate)
        {
            var path = PathOf(name);
            for (var candidate = 0; candidate < 2; candidate++)
            {
                var target = candidate == 0 ? path : path + ".bak";
                if (!File.Exists(target)) continue;
                try
                {
                    return JsonNode.Parse(File.ReadAllText(target));
                }
                catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
                {
                }
            }
            return null;
        }
    }

    public void Write(string name, JsonNode value)
    {
        lock (_gate)
        {
            var path = PathOf(name);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(value.ToJsonString()));
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
    }

    public void AppendHistory(JsonObject entry)
    {
        lock (_gate) File.AppendAllText(PathOf("history.jsonl"), entry.ToJsonString() + "\n");
    }

    public JsonArray ReadHistory(int limit)
    {
        var result = new JsonArray();
        lock (_gate)
        {
            var path = PathOf("history.jsonl");
            if (!File.Exists(path)) return result;
            foreach (var line in File.ReadLines(path).TakeLast(limit).Reverse())
            {
                try
                {
                    var node = JsonNode.Parse(line);
                    if (node != null) result.Add(node);
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }
        }
        return result;
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            var path = PathOf("history.jsonl");
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
