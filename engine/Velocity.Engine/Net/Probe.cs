using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Velocity.Engine.Net;

public sealed class HttpFailure : Exception
{
    public string Code { get; }
    public int Status { get; }
    public long RetryAfterMs { get; }

    public HttpFailure(string code, string message, int status = 0, long retryAfterMs = 0) : base(message)
    {
        Code = code;
        Status = status;
        RetryAfterMs = retryAfterMs;
    }
}

public sealed record RequestAuth
{
    public string? Authorization { get; init; }
    public string? Cookie { get; init; }
    public string? Referer { get; init; }
    public string? UserAgent { get; init; }
    public bool HasCredentials => Authorization != null || Cookie != null;
}

public sealed record ProbeResult
{
    public required Uri OriginalUrl { get; init; }
    public required Uri FinalUrl { get; init; }
    public long Size { get; init; } = -1;
    public bool Ranges { get; init; }
    public string Etag { get; init; } = "";
    public string LastModified { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "";
    public string Encoding { get; init; } = "";
    public string Server { get; init; } = "";
    public string Digest { get; init; } = "";
    public bool IsHtml { get; init; }
    public bool SameOriginAsRequest { get; init; } = true;
    public bool WeakEtag => Etag.StartsWith("W/", StringComparison.Ordinal);
}

public static class HttpProtocol
{
    public const string DefaultAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Velocity/1.0";
    private const int MaxRedirects = 10;

    public static HttpRequestMessage Build(HttpMethod method, Uri url, RequestAuth? auth, bool sameOrigin)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.TryAddWithoutValidation("User-Agent", auth?.UserAgent ?? DefaultAgent);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (auth?.Referer != null) request.Headers.TryAddWithoutValidation("Referer", auth.Referer);
        if (sameOrigin)
        {
            if (auth?.Authorization != null) request.Headers.TryAddWithoutValidation("Authorization", auth.Authorization);
            if (auth?.Cookie != null) request.Headers.TryAddWithoutValidation("Cookie", auth.Cookie);
        }
        return request;
    }

    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    public static (Uri Clean, RequestAuth? Auth) SplitUserInfo(Uri url, RequestAuth? auth)
    {
        if (url.UserInfo.Length == 0) return (url, auth);
        var builder = new UriBuilder(url) { UserName = "", Password = "" };
        var info = Uri.UnescapeDataString(url.UserInfo);
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(info));
        return (builder.Uri, (auth ?? new RequestAuth()) with { Authorization = auth?.Authorization ?? header });
    }

    public static long RetryAfterMs(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header == null) return 0;
        if (header.Delta is { } delta) return (long)Math.Clamp(delta.TotalMilliseconds, 0, 3600000);
        if (header.Date is { } date) return (long)Math.Clamp((date - DateTimeOffset.UtcNow).TotalMilliseconds, 0, 3600000);
        return 0;
    }

    public static async Task<(HttpResponseMessage Response, Uri Final, bool SameOrigin)> SendFollowingAsync(HttpMessageInvoker client, Uri url, RequestAuth? auth, Action<HttpRequestMessage>? prepare, CancellationToken ct)
    {
        var current = url;
        var origin = url;
        var same = true;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = Build(HttpMethod.Get, current, auth, same);
            prepare?.Invoke(request);
            var response = await client.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308)) return (response, current, same);
            var location = response.Headers.Location;
            response.Dispose();
            if (location == null) throw new HttpFailure("bad-redirect", "O servidor pediu um redirecionamento sem destino.", status);
            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (next.Scheme is not ("http" or "https")) throw new HttpFailure("bad-redirect", "O servidor redirecionou para um endereço que não é HTTP.", status);
            same = same && SameOrigin(origin, next);
            current = next;
        }
        throw new HttpFailure("too-many-redirects", "O link redirecionou vezes demais.");
    }

    public static async Task<ProbeResult> ProbeAsync(HttpMessageInvoker client, Uri url, RequestAuth? auth, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var (response, final, same) = await SendFollowingAsync(client, url, auth, r => r.Headers.TryAddWithoutValidation("Range", "bytes=0-0"), timeout.Token);
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status == 416)
            {
                var size = response.Content.Headers.ContentRange?.Length ?? -1;
                if (size == 0) return Describe(response, url, final, 0, false, same);
                var (fallback, finalAgain, sameAgain) = await SendFollowingAsync(client, url, auth, null, timeout.Token);
                using (fallback)
                {
                    Ensure(fallback);
                    return Describe(fallback, url, finalAgain, fallback.Content.Headers.ContentLength ?? -1, false, sameAgain);
                }
            }
            Ensure(response);
            if (status == 206)
            {
                var range = response.Content.Headers.ContentRange;
                if (range is not { HasLength: true, HasRange: true } || range.From != 0)
                    return Describe(response, url, final, range?.Length ?? -1, false, same);
                return Describe(response, url, final, range.Length!.Value, true, same);
            }
            return Describe(response, url, final, response.Content.Headers.ContentLength ?? -1, false, same);
        }
    }

    public static void Ensure(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300) return;
        throw status switch
        {
            401 or 407 => new HttpFailure("needs-auth", "O servidor pediu usuário e senha.", status),
            403 => new HttpFailure("forbidden", "O servidor recusou o acesso (403). O link pode ter expirado ou exigir login.", status),
            404 => new HttpFailure("not-found", "O arquivo não existe mais nesse endereço (404).", status),
            410 => new HttpFailure("gone", "O link expirou (410).", status),
            429 => new HttpFailure("throttled", "O servidor pediu para reduzir as requisições (429).", status, RetryAfterMs(response)),
            503 => new HttpFailure("throttled", "O servidor está sobrecarregado (503).", status, RetryAfterMs(response)),
            _ => new HttpFailure("http", $"O servidor respondeu com erro {status}.", status),
        };
    }

    private static ProbeResult Describe(HttpResponseMessage response, Uri original, Uri final, long size, bool ranges, bool same)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var etag = response.Headers.ETag?.ToString() ?? "";
        var modified = response.Content.Headers.LastModified?.UtcDateTime.ToString("R") ?? "";
        var encoding = string.Join(",", response.Content.Headers.ContentEncoding);
        var disposition = response.Content.Headers.ContentDisposition;
        var name = FileNames.FromResponse(disposition, final, contentType);
        var digest = "";
        if (response.Headers.TryGetValues("Repr-Digest", out var repr)) digest = FileNames.ParseDigest(string.Join(",", repr));
        if (digest.Length == 0 && response.Headers.TryGetValues("Digest", out var legacy)) digest = FileNames.ParseDigest(string.Join(",", legacy));
        if (digest.Length == 0 && response.Content.Headers.ContentMD5 is { Length: 16 } md5 && (int)response.StatusCode == 200) digest = "md5:" + Convert.ToHexString(md5).ToLowerInvariant();
        return new ProbeResult
        {
            OriginalUrl = original,
            FinalUrl = final,
            Size = size,
            Ranges = ranges && encoding.Length == 0 && size > 0,
            Etag = etag,
            LastModified = modified,
            FileName = name,
            ContentType = contentType,
            Encoding = encoding,
            Server = final.Host,
            Digest = digest,
            IsHtml = contentType is "text/html" or "application/xhtml+xml",
            SameOriginAsRequest = same,
        };
    }
}

public static class FileNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$",
    };

    public static string FromResponse(ContentDispositionHeaderValue? disposition, Uri url, string contentType)
    {
        var fromHeader = disposition?.FileNameStar ?? disposition?.FileName;
        var name = Sanitize(fromHeader?.Trim('"') ?? "");
        if (name.Length == 0)
        {
            var path = url.AbsolutePath;
            var last = path[(path.LastIndexOf('/') + 1)..];
            try
            {
                last = Uri.UnescapeDataString(last);
            }
            catch (UriFormatException)
            {
            }
            name = Sanitize(last);
        }
        if (name.Length == 0) name = "download";
        if (Path.GetExtension(name).Length == 0) name += ExtensionFor(contentType);
        return name;
    }

    public static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var slash = raw.LastIndexOfAny(new[] { '/', '\\' });
        var name = slash >= 0 ? raw[(slash + 1)..] : raw;
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c < 32 || c == 127 || "<>:\"|?*/\\".Contains(c) || c is '\u202e' or '\u202d' or '\u2066' or '\u2067' or '\u2068' or '\u2069' or '\u200e' or '\u200f') builder.Append('_');
            else builder.Append(c);
        }
        var clean = builder.ToString().Trim().TrimEnd('.', ' ').TrimStart('.');
        if (clean.Length == 0) return "";
        var stem = clean.Split('.')[0].Trim();
        if (Reserved.Contains(stem)) clean = "_" + clean;
        if (clean.Length > 180)
        {
            var extension = Path.GetExtension(clean);
            if (extension.Length > 20) extension = "";
            clean = clean[..(180 - extension.Length)].TrimEnd('.', ' ') + extension;
        }
        return clean;
    }

    public static string ParseDigest(string header)
    {
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('=');
            if (at <= 0) continue;
            var algorithm = part[..at].Trim().ToLowerInvariant();
            var value = part[(at + 1)..].Trim().Trim(':', '"');
            try
            {
                var hex = Convert.ToHexString(Convert.FromBase64String(value)).ToLowerInvariant();
                if (algorithm is "sha-256" or "sha256") return "sha256:" + hex;
                if (algorithm is "sha-512" or "sha512") return "sha512:" + hex;
                if (algorithm is "md5") return "md5:" + hex;
            }
            catch (FormatException)
            {
            }
        }
        return "";
    }

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "text/html" => ".html",
        "application/zip" => ".zip",
        "application/pdf" => ".pdf",
        "video/mp4" => ".mp4",
        "application/x-msdownload" => ".exe",
        _ => "",
    };
}
