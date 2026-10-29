using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Hushwire.Core;
using Hushwire.Core.AdFilter;

namespace Hushwire.App;

/// <summary>
/// Owns the local YouTube/Twitch ad filter: the private CA (kept in an admin-only folder), its entry in the Windows
/// trust store, and the loopback HTTPS server. Opt-in; every step is reversible with <see cref="RemoveCertificate"/>.
/// </summary>
public sealed class AdFilterService : IDisposable
{
    public const int Port = 443;
    static string CaDir => Path.Combine(Paths.Dir, "ca");
    static string PfxPath => Path.Combine(CaDir, "ca.pfx");

    MitmServer? server;
    readonly ConcurrentDictionary<string, (IPAddress[] Ips, long Expires)> dnsCache = new();

    public bool Running => server?.Running == true;
    public bool Ipv6 => server?.Ipv6Bound == true;
    public string Status { get; private set; } = "Off";

    public static bool CertificateInstalled()
    {
        try
        {
            string? thumb = SavedThumbprint();
            if (thumb is null) return false;
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, false);
            bool any = found.Count > 0;
            foreach (var c in found) c.Dispose();      // each certificate object holds a native handle; don't wait for the finalizer
            return any;
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException) { return false; }
    }

    static string? SavedThumbprint()
    {
        string cer = Path.Combine(CaDir, "ca.cer");
        return File.Exists(cer) ? X509CertificateLoader.LoadCertificate(File.ReadAllBytes(cer)).Thumbprint : null;
    }

    /// <summary>Loads the CA (creating and trusting it the first time) and starts the server. Throws with a readable message.</summary>
    public async Task StartAsync(Func<string, CancellationToken, Task<IPAddress[]>> realResolver, Action<string, int> onRewritten)
    {
        Stop();
        Status = "Starting…";
        LocalCa ca = await Task.Run(EnsureCa);
        await Task.Run(() => EnsureTrusted(ca));

        var resolver = new Func<string, CancellationToken, Task<IPAddress[]>>(async (host, ct) =>
        {
            long now = Environment.TickCount64;
            if (dnsCache.TryGetValue(host, out var c) && c.Expires > now) return c.Ips;
            var ips = await realResolver(host, ct);
            dnsCache[host] = (ips, now + 60_000);
            return ips;
        });
        var s = new MitmServer(ca, resolver, Port) { OnRewritten = onRewritten };
        try { s.Start(); }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            int pid = OwnerTables.FindListener(true, Port);
            string who = pid > 0 ? Attribution.ImagePath(pid) ?? $"PID {pid}" : "another program";
            Status = $"Port {Port} is in use by {who}";
            throw new InvalidOperationException($"The YouTube/Twitch filter needs port {Port}, which is in use by {who}. Close it and try again.");
        }
        server = s;
        Status = "Active";
    }

    public void Stop()
    {
        server?.Dispose(); server = null;
        dnsCache.Clear();
        Status = "Off";
    }

    static LocalCa EnsureCa()
    {
        Directory.CreateDirectory(CaDir);
        // Lock the folder before any key is written (the data folder is readable by every user, and so would be the key), and
        // on every start, so a key left readable by an earlier failed lock-down is fixed too.
        LockDown(CaDir);
        if (File.Exists(PfxPath))
        {
            try
            {
                var existing = LocalCa.Load(File.ReadAllBytes(PfxPath));
                // An expired CA would make every redirected YouTube/Twitch page fail its certificate check.
                if (existing.NotAfter > DateTime.Now.AddDays(30)) return existing;
                existing.Dispose();
            }
            catch (System.Security.Cryptography.CryptographicException) { /* damaged: replace it below */ }
            RemoveFromTrustStore(SavedThumbprintOrNull());     // the replacement gets a new thumbprint; don't leave the old one trusted
        }
        var ca = LocalCa.Create();
        File.WriteAllBytes(PfxPath, ca.ExportPfx());
        File.WriteAllBytes(Path.Combine(CaDir, "ca.cer"), ca.PublicCertificate.Export(X509ContentType.Cert));
        return ca;
    }

    static string? SavedThumbprintOrNull()
    {
        try { return SavedThumbprint(); }
        catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException) { return null; }
    }

    static void RemoveFromTrustStore(string? thumb)
    {
        if (thumb is null) return;
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var c in store.Certificates.Find(X509FindType.FindByThumbprint, thumb, false)) { store.Remove(c); c.Dispose(); }
    }

    /// <summary>The CA's private key must only be readable by administrators and the system.</summary>
    static void LockDown(string dir)
    {
        var psi = new ProcessStartInfo("icacls.exe", $"\"{dir}\" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15000);
        if (p.ExitCode != 0) throw new InvalidOperationException("Could not restrict access to the filter's private key folder.");
    }

    static void EnsureTrusted(LocalCa ca)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, false);
        bool present = existing.Count > 0;
        foreach (var c in existing) c.Dispose();
        if (present) return;
        store.Add(ca.PublicCertificate);
    }

    /// <summary>Stops the filter, removes the CA from the Windows trust store and deletes its keys.</summary>
    public void RemoveCertificate()
    {
        Stop();
        RemoveFromTrustStore(SavedThumbprintOrNull());
        try { if (Directory.Exists(CaDir)) Directory.Delete(CaDir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => Stop();
}
