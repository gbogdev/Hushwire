using System;
using System.IO;
using System.Text;

namespace Hushwire.App;

/// <summary>
/// Append-only text log that cannot grow without limit: when the file passes <c>maxBytes</c> it becomes <c>.1</c>
/// (replacing any older copy) and a fresh file starts, so at most about twice the limit is ever kept. Never throws.
/// </summary>
public sealed class RotatingLog(string path, long maxBytes)
{
    readonly object gate = new();
    public string Path { get; } = path;

    public void Write(string line)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length >= maxBytes) File.Move(Path, Path + ".1", true);
                File.AppendAllText(Path, $"{DateTime.Now:s} {line}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* logging must never be the reason something fails */ }
    }

    public void Write(Exception? e) => Write(e?.ToString() ?? "unknown error");

    /// <summary>The last <paramref name="maxChars"/> characters of the log (newest entries), or "" if there is none.</summary>
    public string Tail(int maxChars = 20_000)
    {
        try
        {
            lock (gate)
            {
                if (!File.Exists(Path)) return "";
                var text = File.ReadAllText(Path, Encoding.UTF8);
                return text.Length <= maxChars ? text : text[^maxChars..];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }
}
