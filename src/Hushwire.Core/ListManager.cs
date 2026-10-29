using System.Buffers;
using System.Net;
using System.Text;

namespace Hushwire.Core;

public sealed class ListSub
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Ads, Trackers, Malware or Custom.</summary>
    public string Category { get; set; } = "Ads";
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; } = 24;
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public string? LastError { get; set; }
    public string? ETag { get; set; }
    public int RuleCount { get; set; }
    public int UnsupportedCount { get; set; }
    public string License { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Builtin { get; set; }
}

public sealed record UpdateResult(bool Ok, bool NotModified, string? Error, int Rules, int Unsupported, string? ETag);

/// <summary>Downloads, validates and atomically stores filter lists. A failed update never touches the live copy.</summary>
public sealed class ListManager
{
    public const long MaxBytes = 64L * 1024 * 1024;
    readonly string dir;
    readonly HttpClient http;

    public ListManager(string dir, HttpClient? http = null)
    {
        this.dir = dir;
        Directory.CreateDirectory(dir);
        this.http = http ?? CreateClient();
    }

    public static HttpClient CreateClient()
    {
        var h = new SocketsHttpHandler
        {
            AllowAutoRedirect = true, MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(90) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Hushwire/" + (typeof(ListManager).Assembly.GetName().Version?.ToString(3) ?? "0"));
        return c;
    }

    public string CurrentPath(string id) => Path.Combine(dir, id + ".txt");
    string PrevPath(string id) => Path.Combine(dir, id + ".prev");

    public static bool IsValidId(string id) => id.Length is > 0 and <= 40 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Contents of the live list, falling back to the previous good copy if the live one is missing or unreadable.</summary>
    public byte[]? ReadBytes(string id)
    {
        foreach (var p in new[] { CurrentPath(id), PrevPath(id) })
        {
            try { if (File.Exists(p)) return File.ReadAllBytes(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* try the previous copy */ }
        }
        return null;
    }

    /// <summary>Total size of the stored lists, used to size the lookup tables once instead of growing them repeatedly.</summary>
    public long StoredBytes(IEnumerable<string> ids)
    {
        long total = 0;
        foreach (var id in ids)
            try { var p = File.Exists(CurrentPath(id)) ? CurrentPath(id) : PrevPath(id); if (File.Exists(p)) total += new FileInfo(p).Length; }
            catch (IOException) { }
        return total;
    }

    public static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(u.Host);

    public async Task<UpdateResult> UpdateAsync(ListSub s, CancellationToken ct = default)
    {
        if (!IsValidId(s.Id)) return Fail("invalid list id");
        if (!IsHttps(s.Url)) return Fail("only https:// URLs are allowed");
        string tmp = Path.Combine(dir, s.Id + ".tmp");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, s.Url);
            if (!string.IsNullOrEmpty(s.ETag) && File.Exists(CurrentPath(s.Id))) req.Headers.TryAddWithoutValidation("If-None-Match", s.ETag);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == HttpStatusCode.NotModified) return new(true, true, null, s.RuleCount, s.UnsupportedCount, s.ETag);
            if (!resp.IsSuccessStatusCode) return Fail($"HTTP {(int)resp.StatusCode}");
            if (resp.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) return Fail("redirected to a non-https URL");
            if (resp.Content.Headers.ContentLength > MaxBytes) return Fail("list too large");

            // one buffer for the whole download: validated and written straight from it, no copies
            var ms = new MemoryStream(resp.Content.Headers.ContentLength is long cl && cl > 0 && cl <= MaxBytes ? (int)cl : 0);
            await using (var body = await resp.Content.ReadAsStreamAsync(ct))
            {
                var buf = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int n;
                    while ((n = await body.ReadAsync(buf, ct)) > 0)
                    {
                        if (ms.Length + n > MaxBytes) return Fail("list exceeds the size limit");
                        ms.Write(buf, 0, n);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buf); }
            }
            ms.TryGetBuffer(out var data);
            var v = Validate(data.AsSpan(), s.RuleCount);
            if (v.Error is not null) return Fail(v.Error);

            await File.WriteAllBytesAsync(tmp, data.AsMemory(), ct);
            if (File.Exists(CurrentPath(s.Id))) File.Replace(tmp, CurrentPath(s.Id), PrevPath(s.Id));
            else File.Move(tmp, CurrentPath(s.Id));
            return new(true, false, null, v.Valid, v.Unsupported, resp.Headers.ETag?.Tag);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Fail(e is TaskCanceledException ? "timed out" : e.Message);
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }

        static UpdateResult Fail(string e) => new(false, false, e, 0, 0, null);
    }

    public sealed record Validation(string? Error, int Valid, int Unsupported);

    struct Counter : ILineHandler
    {
        NullSink sink;
        public int Valid, Invalid, Unsupported;
        public void Line(ReadOnlySpan<char> line)
        {
            switch (RuleParser.ParseCore(line, ref sink))
            {
                case RuleKind.Block or RuleKind.Allow: Valid++; break;
                case RuleKind.Invalid: Invalid++; break;
                case RuleKind.Unsupported: Unsupported++; break;
            }
        }
    }

    /// <summary>Gatekeeping before a download may replace the live list. Works on the raw bytes: no string copy of the file.</summary>
    public static Validation Validate(ReadOnlySpan<byte> data, int previousRules)
    {
        if (!System.Text.Unicode.Utf8.IsValid(data)) return new("not valid UTF-8 text", 0, 0);
        if (data.IndexOf((byte)0) >= 0) return new("binary content", 0, 0);
        var body = data.StartsWith(Utf8Lines.Bom) ? data[3..] : data;
        Span<char> head = stackalloc char[2048];
        int n = Encoding.UTF8.GetChars(body[..Math.Min(body.Length, 2048)], head);
        ReadOnlySpan<char> start = ((ReadOnlySpan<char>)head)[..Math.Min(n, 512)].TrimStart();
        if (start.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            return new("got an HTML page instead of a list", 0, 0);

        var c = new Counter();
        Utf8Lines.ForEach(body, ref c);
        int valid = c.Valid, invalid = c.Invalid, unsupported = c.Unsupported;
        if (valid == 0) return new("no usable rules found", 0, unsupported);
        if (invalid > 0 && invalid * 20 > valid + invalid) return new($"too many malformed lines ({invalid})", valid, unsupported);
        if (previousRules >= 1000 && valid < previousRules / 5) return new($"suspicious shrink ({previousRules} → {valid} rules)", valid, unsupported);
        return new(null, valid, unsupported);
    }
}
