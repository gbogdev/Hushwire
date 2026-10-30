using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Hushwire.Core;

public sealed record QueryEvent(DateTime Time, string Name, ushort QType, Decision Decision, bool Cached, bool Tcp,
    IPEndPoint Remote, int LatencyUs, bool UpstreamFailed);

/// <summary>Loopback DNS proxy: filter → cache → upstream. Binds 127.0.0.1 (required) and ::1 (best effort), UDP+TCP.</summary>
public sealed class DnsProxy : IDisposable
{
    public Func<FilterSnapshot> Snapshot { get; init; } = () => FilterSnapshot.Empty;
    public Func<bool> Active { get; init; } = () => true;
    public Func<IReadOnlyList<IPAddress>> Upstreams { get; init; } = () => [];
    public Func<BlockMode> Mode { get; init; } = () => BlockMode.NullIp;
    public Func<bool> CnameCheck { get; init; } = () => true;
    /// <summary>When true, the names browsers use to switch to their own encrypted DNS are answered "does not exist" (see <see cref="SecureDns"/>).</summary>
    public Func<bool> BlockSecureDns { get; init; } = () => false;
    /// <summary>Encrypted upstream. When set, lookups go there first and fall back to the ordinary upstream on any failure. Change it at any time.</summary>
    public volatile DohUpstream? Doh;
    public long DohFallbacks => Interlocked.Read(ref dohFallbacks);
    long dohFallbacks;
    /// <summary>Only consulted when <see cref="HasExemptApps"/> is true: returns true if the sender is an exempt app.</summary>
    public Func<IPEndPoint, bool, bool>? IsExemptApp { get; init; }
    public Func<bool> HasExemptApps { get; init; } = () => false;
    /// <summary>
    /// Names answered with this PC's own address (the local YouTube/Twitch ad filter). Null or false = resolve normally.
    /// </summary>
    public Func<string, bool>? Redirect { get; init; }
    /// <summary>False when the local filter has no IPv6 listener: AAAA redirects then answer "no address" so clients use IPv4.</summary>
    public Func<bool> RedirectIpv6 { get; init; } = () => true;
    public Action<QueryEvent>? OnQuery { get; init; }
    public Action<string>? OnError { get; init; }
    /// <summary>Upstream DNS port; only tests change it.</summary>
    public int UpstreamPort { get; init; } = 53;
    /// <summary>
    /// How long to wait for an upstream's UDP answer before sending the same query again, per attempt (the sum is the total
    /// patience). UDP is lossy; resolvers retransmit instead of waiting out one long timeout.
    /// </summary>
    public int[] UdpAttemptsMs { get; init; } = [400, 600, 1000];

    public bool Ipv6Bound { get; private set; }
    public int Port { get; }
    public DnsCache Cache { get; } = new();

    readonly CancellationTokenSource cts = new();
    readonly List<Socket> udp = [];
    readonly List<TcpListener> tcp = [];
    readonly SemaphoreSlim inflight = new(512);
    readonly SemaphoreSlim tcpClients = new(128);
    int goodUpstream;

    public DnsProxy(int port = 53) => Port = port;

    /// <summary>Binds all listeners or none: on failure everything already bound is released before the exception propagates.</summary>
    public void Start()
    {
        try
        {
            BindUdp(IPAddress.Loopback);
            BindTcp(IPAddress.Loopback);
            try
            {
                BindUdp(IPAddress.IPv6Loopback);
                BindTcp(IPAddress.IPv6Loopback);
                Ipv6Bound = true;
            }
            catch (SocketException)
            {
                // IPv6 unavailable or ::1:53 taken: continue IPv4-only, caller sees Ipv6Bound=false.
                foreach (var s in udp.Where(s => s.AddressFamily == AddressFamily.InterNetworkV6).ToList()) { udp.Remove(s); s.Dispose(); }
                foreach (var t in tcp.Where(t => t.Server.AddressFamily == AddressFamily.InterNetworkV6).ToList()) { tcp.Remove(t); t.Stop(); }
            }
        }
        catch { Dispose(); throw; }
        foreach (var s in udp) _ = Task.Run(() => UdpLoop(s, cts.Token));
        foreach (var t in tcp) _ = Task.Run(() => TcpLoop(t, cts.Token));
    }

    void BindUdp(IPAddress ip)
    {
        var s = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows()) s.IOControl(-1744830452, [0], null); // SIO_UDP_CONNRESET off
            s.Bind(new IPEndPoint(ip, Port));
            udp.Add(s);
        }
        catch { s.Dispose(); throw; }
    }

    void BindTcp(IPAddress ip)
    {
        var l = new TcpListener(ip, Port);
        l.Start(64);
        tcp.Add(l);
    }

    async Task UdpLoop(Socket s, CancellationToken ct)
    {
        var buf = new byte[4096];
        EndPoint any = s.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await s.ReceiveFromAsync(buf, SocketFlags.None, any, ct);
                var q = buf.AsSpan(0, r.ReceivedBytes).ToArray();
                var remote = (IPEndPoint)r.RemoteEndPoint;
                if (!inflight.Wait(0)) continue; // overloaded: drop, client retries
                _ = HandleUdp(s, q, remote);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { await Task.Delay(20); }
        }
    }

    async Task HandleUdp(Socket s, byte[] q, IPEndPoint remote)
    {
        try
        {
            var resp = await Resolve(q, remote, false, cts.Token);
            if (resp is not null) await s.SendToAsync(resp, SocketFlags.None, remote, cts.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException) { OnError?.Invoke(e.Message); }
        finally { inflight.Release(); }
    }

    async Task TcpLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var c = await l.AcceptTcpClientAsync(ct);
                if (!tcpClients.Wait(0)) { c.Dispose(); continue; }   // too many open: refuse, the client retries
                _ = HandleTcp(c, ct).ContinueWith(_ => tcpClients.Release(), TaskScheduler.Default);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { await Task.Delay(20); }
        }
    }

    async Task HandleTcp(TcpClient c, CancellationToken ct)
    {
        using var _ = c;
        try
        {
            var remote = (IPEndPoint)c.Client.RemoteEndPoint!;
            var st = c.GetStream();
            var lenBuf = new byte[2];
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(10_000);
                await st.ReadExactlyAsync(lenBuf, idle.Token);
                int n = (lenBuf[0] << 8) | lenBuf[1];
                if (n < 12) return;
                var q = new byte[n];
                await st.ReadExactlyAsync(q, idle.Token);
                var resp = await Resolve(q, remote, true, ct);
                if (resp is null) return;
                var o = new byte[resp.Length + 2];
                o[0] = (byte)(resp.Length >> 8); o[1] = (byte)resp.Length;
                Buffer.BlockCopy(resp, 0, o, 2, resp.Length);
                await st.WriteAsync(o, ct);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or EndOfStreamException or SocketException or ObjectDisposedException) { }
        catch (Exception e) { OnError?.Invoke(e.Message); }
    }

    public async Task<byte[]?> Resolve(byte[] q, IPEndPoint remote, bool viaTcp, CancellationToken ct)
    {
        long t0 = Stopwatch.GetTimestamp();
        if (!DnsWire.TryParseQuestion(q, out var name, out var qtype, out var qEnd)) return null;
        int Us() => (int)(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
        ushort id = (ushort)((q[0] << 8) | q[1]);

        Decision d;
        if (!Active()) d = new(Verdict.Allow, Origin.Paused);
        else if (HasExemptApps() && IsExemptApp?.Invoke(remote, viaTcp) == true) d = new(Verdict.Allow, Origin.AppExempt);
        else
        {
            d = Snapshot().Decide(name);
            // your own allow rule still wins: someone may rely on one of these names
            if (d.Origin != Origin.UserAllow && BlockSecureDns() && SecureDns.Matches(name)) d = new(Verdict.Block, Origin.SecureDns, "secure-dns", name);
        }

        if (d.Blocked)
        {
            var b = DnsWire.BuildBlock(q, qEnd, qtype, d.Origin == Origin.SecureDns ? BlockMode.Nxdomain : Mode());
            OnQuery?.Invoke(new(DateTime.Now, name, qtype, d, false, viaTcp, remote, Us(), false));
            return b;
        }

        bool opcodeQuery = ((q[2] >> 3) & 0xF) == 0;
        if (opcodeQuery && qtype is 1 or 28 or 64 or 65 && Redirect?.Invoke(name) == true)
        {
            // Point the browser at the local filter. HTTPS/SVCB records are answered empty so they can't carry an
            // alternative address or encrypted-hello hint that would route around it.
            byte[] r = qtype == 1 ? DnsWire.BuildAddress(q, qEnd, 1, IPAddress.Loopback, 30)
                : qtype == 28 && RedirectIpv6() ? DnsWire.BuildAddress(q, qEnd, 28, IPAddress.IPv6Loopback, 30)
                : DnsWire.BuildNoData(q, qEnd);
            OnQuery?.Invoke(new(DateTime.Now, name, qtype, new(Verdict.Allow, Origin.Redirect, "adfilter", name), false, viaTcp, remote, Us(), false));
            return r;
        }
        // Answers differ by DNSSEC flag and advertised buffer size, so those are part of the key.
        var key = new CacheKey(name, qtype, DnsWire.ParseEdns(q, qEnd) is { } e ? 1u + ((uint)e.Payload << 1 | (e.Do ? 1u : 0u)) : 0u);
        if (opcodeQuery && Cache.TryGet(key, id, out var hit))
        {
            OnQuery?.Invoke(new(DateTime.Now, name, qtype, d, true, viaTcp, remote, Us(), false));
            return hit;
        }

        var resp = await Forward(q, viaTcp, ct);
        if (resp is null)
        {
            OnQuery?.Invoke(new(DateTime.Now, name, qtype, d, false, viaTcp, remote, Us(), true));
            return DnsWire.BuildServFail(q, qEnd);
        }

        var info = DnsWire.Inspect(resp);
        if (info is not null && opcodeQuery)
        {
            if (Active() && CnameCheck())
            {
                var snap = Snapshot();
                foreach (var cn in info.Cnames)
                {
                    var cd = snap.Decide(cn);
                    if (cd.Origin is Origin.ListBlock)
                    {
                        var b = DnsWire.BuildBlock(q, qEnd, qtype, Mode());
                        OnQuery?.Invoke(new(DateTime.Now, name, qtype, new(Verdict.Block, Origin.Cname, cd.ListId, cn), false, viaTcp, remote, Us(), false));
                        return b;
                    }
                }
            }
            Cache.Put(key, resp, info);
        }
        OnQuery?.Invoke(new(DateTime.Now, name, qtype, d, false, viaTcp, remote, Us(), false));
        return resp;
    }

    /// <summary>Real addresses of <paramref name="name"/> straight from the upstream DNS: ignores filtering and redirects.</summary>
    public async Task<IPAddress[]> LookupAsync(string name, CancellationToken ct)
    {
        var found = new List<IPAddress>();
        foreach (ushort type in new ushort[] { 1, 28 })
        {
            var resp = await Forward(DnsWire.BuildQuery(name, type), false, ct);
            if (resp is not null) found.AddRange(DnsWire.ExtractAddresses(resp));
        }
        found = found.Where(a => !IPAddress.IsLoopback(a)).ToList();
        if (found.Count == 0) throw new HttpRequestException($"no address found for {name}");
        // IPv4 first: dual-stack hosts connect faster than waiting on a broken IPv6 path
        return found.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
    }

    async Task<byte[]?> Forward(byte[] q, bool viaTcp, CancellationToken ct)
    {
        var doh = Doh;
        if (doh is not null)
        {
            var secure = await doh.QueryAsync(q, ct);
            if (secure is not null) return secure;
            Interlocked.Increment(ref dohFallbacks);      // the encrypted server didn't answer: use the ordinary upstream so DNS keeps working
        }
        var ups = Upstreams();
        int n = ups.Count;
        for (int i = 0; i < n; i++)
        {
            int idx = (goodUpstream + i) % n;
            try
            {
                var r = viaTcp ? await TcpQuery(ups[idx], q, ct) : await UdpQuery(ups[idx], q, ct);
                if (r is not null) { goodUpstream = idx; return r; }
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested) { }
        }
        return null;
    }

    async Task<byte[]?> UdpQuery(IPAddress ip, byte[] q, CancellationToken ct)
    {
        using var s = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await s.ConnectAsync(ip, UpstreamPort, ct);
        var buf = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (int waitMs in UdpAttemptsMs)
            {
                await s.SendAsync(q, SocketFlags.None, ct);          // first try, then the same query again if no answer came
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(waitMs);
                try
                {
                    while (true)
                    {
                        int n = await s.ReceiveAsync(buf, SocketFlags.None, attempt.Token);
                        if (n >= 12 && buf[0] == q[0] && buf[1] == q[1] && (buf[2] & 0x80) != 0) return buf.AsSpan(0, n).ToArray();
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* nothing yet: send again */ }
            }
            return null;
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    async Task<byte[]?> TcpQuery(IPAddress ip, byte[] q, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(3000);
        using var c = new TcpClient(ip.AddressFamily);
        await c.ConnectAsync(ip, UpstreamPort, cts.Token);
        var st = c.GetStream();
        var o = new byte[q.Length + 2];
        o[0] = (byte)(q.Length >> 8); o[1] = (byte)q.Length;
        Buffer.BlockCopy(q, 0, o, 2, q.Length);
        await st.WriteAsync(o, cts.Token);
        var l = new byte[2];
        await st.ReadExactlyAsync(l, cts.Token);
        var r = new byte[(l[0] << 8) | l[1]];
        await st.ReadExactlyAsync(r, cts.Token);
        return r.Length >= 12 && r[0] == q[0] && r[1] == q[1] ? r : null;
    }

    public void Dispose()
    {
        cts.Cancel();
        foreach (var s in udp) s.Dispose();
        foreach (var t in tcp) t.Stop();
    }
}
