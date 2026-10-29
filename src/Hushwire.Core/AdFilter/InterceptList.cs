namespace Hushwire.Core.AdFilter;

public enum RewriteKind { None, YouTubeJson, YouTubeHtml, TwitchPlaylist }

/// <summary>Which hostnames get redirected to the local filter, and which responses on them are rewritten.</summary>
public static class InterceptList
{
    public static bool IsYouTube(string host) => host is "www.youtube.com" or "m.youtube.com";

    public static bool IsTwitchHls(string host) => host == "usher.ttvnw.net" || host.EndsWith(".hls.ttvnw.net", StringComparison.Ordinal);

    public static bool Matches(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return IsYouTube(host) || IsTwitchHls(host);
    }

    public static RewriteKind Classify(string host, string pathAndQuery, string? mediaType)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        mediaType = mediaType?.ToLowerInvariant() ?? "";
        if (IsYouTube(host))
        {
            if (mediaType.Contains("json")) return RewriteKind.YouTubeJson;
            if (mediaType == "text/html") return RewriteKind.YouTubeHtml;
        }
        else if (IsTwitchHls(host))
        {
            int q = pathAndQuery.IndexOf('?');
            string path = q < 0 ? pathAndQuery : pathAndQuery[..q];
            if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("mpegurl")) return RewriteKind.TwitchPlaylist;
        }
        return RewriteKind.None;
    }
}
