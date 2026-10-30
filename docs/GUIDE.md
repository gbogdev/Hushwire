# Hushwire guide

The details behind the [README](../README.md): how filtering works, the filter lists, the optional YouTube/Twitch filter, privacy and safety, building from source, and project status.

## How it works

```
 program ──DNS query──▶ 127.0.0.1:53   Hushwire
                         │  1. your allow/block rules, then protected Windows domains,
                         │     then list exceptions, then list blocks
                         │  2. blocked?  → answer 0.0.0.0 / ::  (nothing leaves the PC)
                         │  3. allowed?  → cache, otherwise ask your real DNS servers
                         │     (an unanswered UDP query is re-sent after 400 ms, 600 ms, 1 s)
                         │  4. CNAME chain checked: a blocked alias blocks the whole answer
                         ▼
                   original / chosen upstream DNS
```

- **Matching** is a hash lookup per parent domain (`a.b.example.com`, `b.example.com`, `example.com`) from a single pass over the name. Rules are stored as 64-bit hashes,
  ~34 bytes each, so over a million rules fit in ~36 MB.
- **Hosts-file entries** match that exact name; **domain-list and `||domain^` entries** match the domain and all its subdomains.
- **Supported list syntax** is the DNS-relevant subset. Cosmetic, URL-path and regex rules can't be applied at DNS level; they are counted and skipped, never executed. Lists are data, not code.
- **DNS settings** are changed through the Windows IP Helper API, read back and verified, with PowerShell kept as a fallback.

## Filter lists

Everything in the catalog was downloaded and parsed with Hushwire's own parser when added. Only the first row is on by default.

| Category | Lists |
|---|---|
| **On by default** | StevenBlack Unified Hosts · EasyPrivacy · URLhaus malware domains |
| Ads | HaGeZi Light / Pro · OISD small / big · AdGuard DNS filter · AdAway · Peter Lowe's · Anudeep's · 1Hosts Lite · KADhosts · Disconnect (advertising) · Admiral · EasyList (hosts form) |
| Trackers | First-party (CNAME-cloaked) trackers · Smart-TV tracking · Android tracking |
| Malware & scams | Phishing Army · malware-filter phishing · DandelionSprout anti-malware · Scam blocklist · Spam404 · NoCoin (cryptojacking) · HaGeZi threat intelligence (medium) |
| Windows telemetry (**off**, can reduce Windows features) | WindowsSpyBlocker · HaGeZi Windows/Office |

Lists are downloaded by your PC from each publisher at runtime; Hushwire doesn't redistribute them, and each list's license is shown in the app. After the defaults, **HaGeZi Pro** or **OISD small**
are sensible next steps — add one at a time so any breakage is easy to trace.

## YouTube & Twitch ads inside the page (optional, off by default)

DNS can't touch these ads because they come from the same servers as the video, so Hushwire has an opt-in local filter (*Settings → YouTube & Twitch ads inside the page*).
Hushwire answers a few streaming hostnames (`www.youtube.com`, `m.youtube.com`, Twitch's `usher.ttvnw.net` and `*.hls.ttvnw.net`) with this PC's own address; a loopback-only HTTPS server presents
a certificate from a private CA, forwards each request to the real site, and removes ad data from YouTube's player JSON and page data and from Twitch's playlists. Everything else is relayed unchanged.

- The CA is **name-constrained to `youtube.com` and `ttvnw.net`** — Windows rejects anything else it signs — created on first use, kept in an admin-only folder, and installed into
  `LocalMachine\Root`. *Turn off and remove the certificate* undoes all of it.
- Works in browsers that use the Windows certificate store (Chrome, Edge, Brave…). **Not Firefox** (own store); not apps that pin certificates.
- Needs port 443 free. Only active while protection is on; pausing turns it off. Not available in `--dev` mode.
- **Experimental and fragile.** YouTube and Twitch change their formats and actively fight blockers; the Twitch part follows publicly described playlist formats and hasn't been tested against a live ad break.

## Privacy and safety

- **This program will not transfer any information to other networked systems unless specifically requested by the user.** There is no telemetry, no account, no cloud and no update check. The only network connections Hushwire makes are DNS to your chosen upstream (or, only if you turn on *Settings → Secure DNS → encrypted upstream*, HTTPS to the DNS-over-HTTPS provider you pick: Cloudflare, Quad9 or Google, reached by built-in address with normal certificate checks) and downloads from the list URLs you enabled. Statistics and the activity log stay on this PC
  (`%ProgramData%\Hushwire`, administrators only). The log records blocked lookups (allowed ones only if you switch that on); clear it any time.
- List downloads require HTTPS and normal certificate validation; redirects to non-HTTPS are refused. Lists are parsed as data only. Settings and imports are validated; malformed values are dropped rather than trusted.
- The DNS safety net (journal, watchdog, boot task, repair shortcut, `--restore`) exists because pointing DNS at a program that isn't running would break the internet.

## Development

Requires the **.NET 9 SDK** on Windows.

```
dotnet run --project src/Hushwire.App -- --dev      # dev mode: port 5300, never touches your network settings
.\scripts\build-release.ps1                              # publishes dist\Hushwire.exe (0.5 MB) and dist\standalone\Hushwire.exe (155 MB)
installer\build-installer.ps1                    # the MSI (add -Standalone for the self-contained one)
dotnet run -c Release --project bench/Hushwire.Bench -- all   # benchmarks (also: latency <dns ip>, parse)
powershell -File scripts\render-assets.ps1         # re-render the logo/banner PNGs and the app icon from assets\*.svg
```

| Flag / variable | Meaning |
|---|---|
| `--dev` | Listen on `127.0.0.1:5300`; never change adapter DNS, no certificate, no scheduled tasks. Use with Debug builds (they don't need admin). |
| `--tray` | Start hidden in the tray |
| `--restore` | Headless: put DNS back from the journal (also what the boot task runs) |
| `--stop` | Headless: ask a running Hushwire to exit cleanly and wait (the installer uses it before upgrades and uninstall) |
| `--uninstall` | Headless: restore DNS, remove the scheduled tasks, the certificate and Hushwire's data (the installer runs it on uninstall) |
| `--snapshot <dir>` | Dev aid: render every screen to PNGs and exit |
| `HUSHWIRE_DATA=<dir>` | Use another data folder instead of `%ProgramData%\Hushwire` |

**Layout**

| Path | Contents |
|---|---|
| `src/Hushwire.Core` | Platform-free engine: rule parser, hashed rule tables, DNS wire codec, cache, proxy, list manager, catalog, YouTube/Twitch rewriter, local CA and HTTPS filter |
| `src/Hushwire.App` | WPF tray app: protection orchestration, DNS takeover/restore, attribution, stats, UI |
| `bench/Hushwire.Bench` | Repeatable performance measurements |
| `scripts/` | `build-release.ps1` (publish), `render-assets.ps1` (logo/icon rendering) |
| `installer/` | WiX 5 definition and build script (WiX is a repo-local dotnet tool: `dotnet tool restore`) |
| `assets/` | Logo, banner (SVG sources and PNG renders) |
| `docs/` | `PERFORMANCE.md` |

## Status

**Verified:** rule parsing and precedence, DNS codec (fuzzed), cache, CNAME uncloaking, list validation, the proxy end to end over real sockets and under simulated packet loss, the local CA's name constraints,
the HTTPS filter against a fake origin, UI rendering, DNS takeover/restore and a real MSI install on the author's Windows 11 PC, performance (see above).

**Not yet verified:** the YouTube/Twitch filter against the live sites; MSI upgrade and uninstall on a real install; VPN, sleep/resume and multi-adapter
behaviour; Windows 10.

**Planned:** a background Windows service with an unprivileged UI, a wizard-style installer, automatic updates, DNS-over-HTTPS upstreams, optional Windows Filtering Platform enforcement against DNS bypass,
SQLite activity history, code signing.

## Secure DNS

Two opt-in settings under *Settings → Secure DNS*:

- **Stop browsers from bypassing Hushwire.** Chrome, Edge and Firefox can switch to their own encrypted DNS and skip the system resolver. When this is on, Hushwire answers Firefox's canary name (`use-application-dns.net`) and the well-known DoH provider hostnames as "does not exist". Browsers in their default (automatic) mode then keep using the system DNS. A browser forced into *strict* secure-DNS mode shows an error instead, and software that deliberately talks to those providers (for example Cloudflare WARP) can be affected, so it is off by default. Your own allow rules override it.
- **Encrypted upstream.** Allowed lookups are sent to a DNS-over-HTTPS server (RFC 8484) instead of in plain text. The provider is contacted by built-in address, so no DNS lookup is needed to find it, and its certificate is verified. If it doesn't answer within 4 seconds, or answers wrongly (not a response, or for a different question), Hushwire falls back to your ordinary upstream so DNS keeps working behind a captive portal or VPN. The diagnostics page shows how many lookups were answered and how many fell back. The provider you choose sees your lookups instead of your network.
