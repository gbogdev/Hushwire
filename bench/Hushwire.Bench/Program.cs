// Performance harness. Run:  dotnet run -c Release --project bench/Hushwire.Bench [parse|decide|validate|proxy|stats|all]
// Downloads the sample lists once into %TEMP%\hushwire-bench. Numbers are for comparing before/after on the same PC.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Hushwire.App;
using Hushwire.Core;

static class P
{
    static readonly string Dir = Path.Combine(Path.GetTempPath(), "hushwire-bench");
    static readonly string[] Sample = ["stevenblack", "easyprivacy", "urlhaus", "hagezi-pro", "oisd-big", "phishing-army"];

    static async Task<Dictionary<string, string>> EnsureLists()
    {
        // `full` uses every list the installed app has downloaded (all 28 when enabled), the heaviest realistic setup
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hushwire", "lists");
        if (Environment.GetEnvironmentVariable("HUSHWIRE_BENCH_FULL") == "1" && Directory.Exists(installed))
            return Directory.GetFiles(installed, "*.txt").ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => f);
        Directory.CreateDirectory(Dir);
        var http = ListManager.CreateClient();
        var files = new Dictionary<string, string>();
        foreach (var l in ListCatalog.All().Where(l => Sample.Contains(l.Id)))
        {
            var f = Path.Combine(Dir, l.Id + ".txt");
            if (!File.Exists(f)) await File.WriteAllBytesAsync(f, await http.GetByteArrayAsync(l.Url));
            files[l.Id] = f;
        }
        return files;
    }

    static double Mb(long b) => b / 1048576.0;
    static long Heap() { GC.Collect(2, GCCollectionMode.Forced, true, true); return GC.GetTotalMemory(true); }

    static async Task Main(string[] args)
    {
        string what = args.Length > 0 ? args[0] : "all";
        if (what == "latency") { await Latency(args.Length > 1 ? IPAddress.Parse(args[1]) : throw new ArgumentException("usage: latency <upstream ip>")); return; }
        if (what == "dns-read")   // read-only check of the native DNS API against what Windows reports
        {
            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up))
            {
                if (!Guid.TryParse(n.Id, out var g)) continue;
                Console.WriteLine($"{n.Name,-40} v4=[{string.Join(",", NativeDns.Read(g, false) ?? new[] { "?unreadable" })}] v6=[{string.Join(",", NativeDns.Read(g, true) ?? new[] { "?unreadable" })}]");
            }
            return;
        }
        var files = await EnsureLists();
        Console.WriteLine($"lists: {string.Join(", ", files.Select(f => $"{f.Key} {new FileInfo(f.Value).Length / 1024} KB"))}");
        FilterSnapshot? snap = null;
        if (what is "all" or "parse" or "decide" or "proxy") snap = Parse(files);
        if (what is "all" or "decide") Decide(snap!, files);
        if (what is "all" or "validate") Validate(files);
        if (what is "all" or "stats") StatsBench();
        if (what is "all" or "proxy") await Proxy(snap!);
        Console.WriteLine($"peak working set {Mb(Process.GetCurrentProcess().PeakWorkingSet64):0} MB");
    }

    static FilterSnapshot Parse(Dictionary<string, string> files)
    {
        Console.WriteLine("\n== parse + build snapshot (6 lists)");
        int handles0 = Process.GetCurrentProcess().HandleCount;
        long before = Heap();
        long alloc0 = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        var b = new FilterBuilder((int)(files.Values.Sum(f => new FileInfo(f).Length) / 20));
        int lines = 0;
        foreach (var (id, path) in files.Where(f => f.Key != "x"))
        {
            var bytes = File.ReadAllBytes(path);               // the production path: bytes in, no string per line
            lines += bytes.AsSpan().Count((byte)10);
            b.AddListUtf8(id, bytes);
        }
        var snap = b.Build(["a.example.com"], ["b.example.com"]);
        sw.Stop();
        long allocated = GC.GetTotalAllocatedBytes(true) - alloc0;
        long retained = Heap() - before;
        Console.WriteLine($"{lines:N0} lines -> {snap.RuleCount:N0} rules in {sw.ElapsedMilliseconds} ms  ({lines / sw.Elapsed.TotalSeconds / 1e6:0.00} M lines/s)");
        Console.WriteLine($"allocated {Mb(allocated):0} MB while building, retained {Mb(retained):0} MB ({retained / (double)snap.RuleCount:0} B/rule)");
        Console.WriteLine($"handles {handles0} -> {Process.GetCurrentProcess().HandleCount}, GC heap now {Mb(GC.GetTotalMemory(false)):0} MB");
        return snap;
    }

    static void Decide(FilterSnapshot snap, Dictionary<string, string> files)
    {
        Console.WriteLine("\n== Decide()");
        var rnd = new Random(1);
        var names = new List<string>();
        foreach (var line in File.ReadLines(files["stevenblack"]))
        {
            var p = line.Split(' ');
            if (p.Length == 2 && p[0] == "0.0.0.0" && p[1].Contains('.')) names.Add(p[1]);
        }
        var blocked = names.OrderBy(_ => rnd.Next()).Take(20000).ToArray();
        var allowed = Enumerable.Range(0, 20000).Select(i => $"www{i}.some-site-{i % 977}.example.org").ToArray();
        var mixed = blocked.Select(n => "cdn." + n).Concat(allowed).Concat(blocked).ToArray();
        foreach (var (label, set) in new[] { ("blocked (exact)", blocked), ("allowed (miss)", allowed), ("subdomain of blocked", mixed) })
        {
            for (int i = 0; i < 20000; i++) snap.Decide(set[i % set.Length]);   // warm
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            int n = 2_000_000, hits = 0;
            for (int i = 0; i < n; i++) if (snap.Decide(set[i % set.Length]).Blocked) hits++;
            sw.Stop();
            long a = GC.GetAllocatedBytesForCurrentThread() - a0;
            Console.WriteLine($"{label,-22} {sw.Elapsed.TotalMilliseconds * 1e6 / n,7:0} ns/op  {a / (double)n,6:0.0} B/op  ({hits:N0} blocked)");
        }
    }

    static void Validate(Dictionary<string, string> files)
    {
        Console.WriteLine("\n== ListManager.Validate (largest sample list)");
        var big = files.Values.OrderByDescending(f => new FileInfo(f).Length).First();
        var bytes = File.ReadAllBytes(big);
        long before = Heap(); long a0 = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        var v = ListManager.Validate(bytes, 0);
        sw.Stop();
        Console.WriteLine($"{Path.GetFileName(big)} {bytes.Length / 1024} KB: {v.Valid:N0} rules in {sw.ElapsedMilliseconds} ms, allocated {Mb(GC.GetTotalAllocatedBytes(true) - a0):0} MB");
    }

    static void StatsBench()
    {
        Console.WriteLine("\n== Stats.Record");
        var s = new Stats();
        var app = new AppInfo(1, @"C:\x\app.exe", "App", true);
        var domains = Enumerable.Range(0, 5000).Select(i => $"ads{i}.tracker.net").ToArray();
        for (int i = 0; i < 100000; i++) s.Record(domains[i % 5000], i % 3 == 0, false, false, false, "Ads", "List", app);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        int n = 1_000_000;
        for (int i = 0; i < n; i++) s.Record(domains[i % 5000], i % 3 == 0, i % 5 == 0, false, false, "Ads", "List", app);
        sw.Stop();
        Console.WriteLine($"{sw.Elapsed.TotalMilliseconds * 1e6 / n:0} ns/op  {(GC.GetAllocatedBytesForCurrentThread() - a0) / (double)n:0.0} B/op");
    }

    sealed class Upstream : IDisposable
    {
        readonly UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        readonly CancellationTokenSource cts = new();
        public int Port => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        public Upstream()
        {
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var r = await udp.ReceiveAsync(cts.Token);
                        var q = r.Buffer; DnsWire.TryParseQuestion(q, out _, out _, out var qEnd);
                        var resp = new byte[qEnd + 16]; Buffer.BlockCopy(q, 0, resp, 0, qEnd);
                        resp[2] = 0x81; resp[3] = 0x80; resp[7] = 1; resp[10] = resp[11] = 0;
                        int p = qEnd; resp[p++] = 0xC0; resp[p++] = 0x0C; resp[p++] = 0; resp[p++] = 1; resp[p++] = 0; resp[p++] = 1;
                        resp[p++] = 0; resp[p++] = 0; resp[p++] = 1; resp[p++] = 0x2C; resp[p++] = 0; resp[p++] = 4; resp[p++] = 1; resp[p++] = 2; resp[p++] = 3; resp[p++] = 4;
                        await udp.SendAsync(resp, resp.Length, r.RemoteEndPoint);
                    }
                    catch { break; }
                }
            });
        }
        public void Dispose() { cts.Cancel(); udp.Dispose(); }
    }

    static byte[] Q(string name, ushort id)
    {
        var q = DnsWire.BuildQuery(name, 1); q[0] = (byte)(id >> 8); q[1] = (byte)id; return q;
    }

    static async Task Proxy(FilterSnapshot snap)
    {
        Console.WriteLine("\n== DnsProxy over real UDP sockets (loopback), 32 concurrent clients");
        using var up = new Upstream();
        int events = 0;
        int port;
        { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        using var proxy = new DnsProxy(port)
        {
            Snapshot = () => snap, Upstreams = () => [IPAddress.Loopback], UpstreamPort = up.Port,
            OnQuery = e => Interlocked.Increment(ref events),
        };
        proxy.Start();

        foreach (var (label, makeName) in new (string, Func<int, string>)[]
        {
            ("blocked", i => "ads" + i % 50 + ".doubleclick.net"),
            ("cache hits", i => "site" + i % 200 + ".example.org"),
            ("cache misses (upstream)", i => "u" + i + ".unique.example.net"),
        })
        {
            var lat = new List<double>(); var latLock = new object();
            int total = label.StartsWith("cache misses") ? 20000 : 100000, perClient = total / 32;
            // prime the cache for the "hits" scenario
            if (label == "cache hits") await RunClients(proxy, port, 1, 200, makeName, null);
            long a0 = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            await RunClients(proxy, port, 32, perClient, makeName, lat);
            sw.Stop();
            lat.Sort();
            double Pct(double p) => lat[(int)(lat.Count * p)] * 1000;
            Console.WriteLine($"{label,-24} {total / sw.Elapsed.TotalSeconds,9:N0} qps   p50 {Pct(.5),6:0} us  p99 {Pct(.99),7:0} us   {(GC.GetTotalAllocatedBytes(true) - a0) / (double)total,6:0} B/query (client+server)");
        }
    }

    static async Task RunClients(DnsProxy proxy, int port, int clients, int perClient, Func<int, string> name, List<double>? lat)
    {
        await Task.WhenAll(Enumerable.Range(0, clients).Select(c => Task.Run(async () =>
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(IPAddress.Loopback, port);
            var buf = new byte[1500];
            var local = lat is null ? null : new List<double>(perClient);
            for (int i = 0; i < perClient; i++)
            {
                var q = Q(name(c * perClient + i), (ushort)i);
                long t = Stopwatch.GetTimestamp();
                await s.SendAsync(q);
                using var cts = new CancellationTokenSource(3000);
                try { await s.ReceiveAsync(buf, cts.Token); } catch (OperationCanceledException) { continue; }
                local?.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }
            if (lat is not null) lock (lat) lat.AddRange(local!);
        })));
    }

    // One client, one query at a time: the delay a program actually notices. Hushwire in front of a real upstream vs asking it directly.
    static async Task Latency(IPAddress upstream)
    {
        var files = await EnsureLists();
        var b = new FilterBuilder();
        foreach (var (id, path) in files) b.AddListUtf8(id, File.ReadAllBytes(path));
        var snap = b.Build([], []);
        string[] popular = ("google.com youtube.com facebook.com wikipedia.org amazon.com twitter.com instagram.com linkedin.com reddit.com netflix.com microsoft.com apple.com " +
            "github.com stackoverflow.com bing.com yahoo.com twitch.tv spotify.com adobe.com cloudflare.com mozilla.org bbc.co.uk cnn.com nytimes.com theguardian.com ebay.com paypal.com " +
            "dropbox.com zoom.us slack.com discord.com steampowered.com epicgames.com ubisoft.com ea.com roblox.com minecraft.net office.com live.com outlook.com whatsapp.com telegram.org " +
            "imdb.com tripadvisor.com booking.com airbnb.com etsy.com aliexpress.com walmart.com target.com bestbuy.com ikea.com nvidia.com amd.com intel.com python.org nodejs.org rust-lang.org " +
            "docker.com kubernetes.io npmjs.com nuget.org gitlab.com bitbucket.org medium.com quora.com pinterest.com tumblr.com flickr.com vimeo.com soundcloud.com bandcamp.com").Split(' ');
        var names = popular.SelectMany(d => new[] { d, "www." + d }).ToArray();
        var adNames = File.ReadLines(files["stevenblack"]).Select(l => l.Split(' ')).Where(p => p.Length == 2 && p[0] == "0.0.0.0" && p[1].Contains('.')).Select(p => p[1]).Take(150).ToArray();

        int port; { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        using var proxy = new DnsProxy(port) { Snapshot = () => snap, Upstreams = () => [upstream] };
        proxy.Start();

        async Task<List<double>> Run(IPAddress ip, int p, IEnumerable<string> list)
        {
            using var s = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(ip, p);
            var times = new List<double>(); var buf = new byte[1500]; ushort id = 0;
            foreach (var n in list)
            {
                var q = Q(n, id++);
                long t = Stopwatch.GetTimestamp();
                await s.SendAsync(q);
                using var cts = new CancellationTokenSource(2500);
                try { await s.ReceiveAsync(buf, cts.Token); times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds); } catch (OperationCanceledException) { }
            }
            times.Sort(); return times;
        }
        void Print(string label, List<double> t) =>
            Console.WriteLine($"{label,-44} n={t.Count,4}  p50 {t[t.Count / 2],7:0.00} ms   p95 {t[(int)(t.Count * .95)],7:0.00} ms   p99 {t[(int)(t.Count * .99)],7:0.00} ms   max {t[^1],7:0.00} ms");

        await Run(upstream, 53, names.Take(10));                             // warm the path
        await Run(IPAddress.Loopback, port, Enumerable.Range(0, 60).Select(i => $"warmup{i}.example.com"));   // let the runtime compile Hushwire's code paths first
        Console.WriteLine($"{Environment.NewLine}== latency, one query at a time, upstream {upstream}");
        var direct1 = await Run(upstream, 53, names);
        var viaCold = await Run(IPAddress.Loopback, port, names);            // Hushwire has never seen these: forwards upstream
        var direct2 = await Run(upstream, 53, names);
        var viaWarm = await Run(IPAddress.Loopback, port, names);            // now answered from Hushwire's cache
        var blocked = await Run(IPAddress.Loopback, port, adNames);
        Print("asking the upstream DNS directly", direct1.Concat(direct2).OrderBy(x => x).ToList());
        Print("through Hushwire, first time (forwarded)", viaCold);
        Print("through Hushwire, repeat (cached by Hushwire)", viaWarm);
        Print("ad/tracker names through Hushwire (blocked)", blocked);
    }
}
