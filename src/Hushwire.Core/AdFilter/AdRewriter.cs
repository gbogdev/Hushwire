using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hushwire.Core.AdFilter;

/// <summary>
/// Removes ad data from YouTube/Twitch responses. Pure functions over text so they are testable without a network.
/// These formats are controlled by the sites and change often; every function returns its input unchanged on anything
/// unexpected rather than risk breaking playback.
/// </summary>
public static class AdRewriter
{
    // Keys of a YouTube player response that describe ads to play.
    static readonly string[] PlayerAdKeys = ["adPlacements", "playerAds", "adSlots", "adBreakHeartbeatParams"];

    // Renderers YouTube uses for ad items inside feeds, search results and the watch-next column.
    static readonly HashSet<string> AdRenderers = new(StringComparer.Ordinal)
    {
        "adSlotRenderer", "displayAdRenderer", "inFeedAdLayoutRenderer", "promotedSparklesWebRenderer",
        "promotedSparklesTextSearchRenderer", "compactPromotedVideoRenderer", "promotedVideoRenderer",
        "bannerPromoRenderer", "searchPyvRenderer", "adBreakServiceRenderer", "videoMastheadAdV3Renderer",
    };

    static readonly JsonSerializerOptions Out = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static bool IsAdItem(JsonNode? el)
    {
        if (el is not JsonObject o) return false;
        foreach (var (k, v) in o)
        {
            if (AdRenderers.Contains(k)) return true;
            if (v is JsonObject o2)
                foreach (var (k2, v2) in o2)
                {
                    if (AdRenderers.Contains(k2)) return true;   // { richItemRenderer: { adSlotRenderer } }
                    if (v2 is JsonObject o3)
                        foreach (var k3 in o3.Select(p => p.Key))
                            if (AdRenderers.Contains(k3)) return true; // { richItemRenderer: { content: { adSlotRenderer } } }
                }
        }
        return false;
    }

    /// <summary>Removes ad data in place; returns how many things were removed.</summary>
    public static int PruneYouTube(JsonNode? node, int depth = 0)
    {
        if (node is null || depth > 60) return 0;
        int removed = 0;
        if (node is JsonArray arr)
        {
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (IsAdItem(arr[i])) { arr.RemoveAt(i); removed++; }
                else removed += PruneYouTube(arr[i], depth + 1);
            }
        }
        else if (node is JsonObject obj)
        {
            foreach (var k in PlayerAdKeys) if (obj.Remove(k)) removed++;
            foreach (var child in obj.Select(p => p.Value).ToList()) removed += PruneYouTube(child, depth + 1);
        }
        return removed;
    }

    /// <summary>JSON text in, JSON text out; unchanged (removed = 0) if it isn't JSON or contains no ads.</summary>
    public static string PruneYouTubeJson(string json, out int removed)
    {
        removed = 0;
        var t = json.AsSpan().TrimStart();
        if (t.Length < 2 || (t[0] != '{' && t[0] != '[')) return json;
        try
        {
            var node = JsonNode.Parse(json);
            removed = PruneYouTube(node);
            return removed > 0 ? node!.ToJsonString(Out) : json;
        }
        catch (JsonException) { removed = 0; return json; }
    }

    static readonly Regex InlineJson = new(@"\b(ytInitialPlayerResponse|ytInitialData)\s*=\s*(?=\{)", RegexOptions.Compiled);

    /// <summary>YouTube pages embed the player response and page data as JSON inside a script; clean those in place.</summary>
    public static string PruneYouTubeHtml(string html, out int removed)
    {
        removed = 0;
        var matches = InlineJson.Matches(html);
        if (matches.Count == 0) return html;
        var sb = new StringBuilder(html);
        for (int m = matches.Count - 1; m >= 0; m--) // back to front so earlier offsets stay valid
        {
            int start = matches[m].Index + matches[m].Length;
            int end = EndOfJsonObject(html, start);
            if (end < 0) continue;
            string pruned = PruneYouTubeJson(html.Substring(start, end - start), out int n);
            if (n == 0) continue;
            sb.Remove(start, end - start).Insert(start, pruned);
            removed += n;
        }
        return removed > 0 ? sb.ToString() : html;
    }

    /// <summary>Index just past the closing brace of the object starting at <paramref name="start"/>, or -1.</summary>
    internal static int EndOfJsonObject(string s, int start)
    {
        int depth = 0;
        bool inStr = false, esc = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
            }
            else if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i + 1;
        }
        return -1;
    }

    static readonly Regex AdMarker = new(@"stitched-ad|X-TV-TWITCH-AD", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex AdRange = new(@"^#EXT-X-DATERANGE:.*(stitched-ad|X-TV-TWITCH-AD)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ExtInf = new(@"^#EXTINF:[\d.]+,(.*)$", RegexOptions.Compiled);

    /// <summary>
    /// Twitch inserts ads into the live playlist ("stitched ads"). When the playlist announces one, drop the segments that are
    /// not regular live content. If nothing playable would remain, returns the playlist unchanged. EXPERIMENTAL.
    /// </summary>
    public static string StripTwitchAds(string playlist, out int removed)
    {
        removed = 0;
        if (!AdMarker.IsMatch(playlist)) return playlist;
        var lines = playlist.Split('\n');
        var output = new List<string>(lines.Length);
        int kept = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string t = line.TrimEnd('\r');
            if (AdRange.IsMatch(t) || t.StartsWith("#EXT-X-SCTE35", StringComparison.OrdinalIgnoreCase)) { removed++; continue; }
            var m = ExtInf.Match(t);
            if (m.Success)
            {
                string title = m.Groups[1].Value.Trim();
                bool live = title.Length == 0 || title.Equals("live", StringComparison.OrdinalIgnoreCase);
                if (!live && i + 1 < lines.Length) { i++; removed++; continue; } // drop the EXTINF line and its segment URL
                if (live) kept++;
            }
            output.Add(line);
        }
        if (kept == 0) { removed = 0; return playlist; }
        return string.Join('\n', output);
    }
}
