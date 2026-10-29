<p align="center">
  <img src="assets/banner.png" alt="Hushwire: system-wide ad, tracker and malware blocking for Windows" width="100%">
</p>

**Hushwire** is a small Windows app that blocks ads, trackers and known-bad sites for **every program on your PC** — browsers, launchers,
games, apps — by answering their DNS lookups itself. No browser extension, no account, no subscription, no telemetry. It sits in the tray, uses
about as much CPU as a clock, and takes one click to switch off.

[![build](https://github.com/gbogdev/Hushwire/actions/workflows/build.yml/badge.svg)](https://github.com/gbogdev/Hushwire/actions/workflows/build.yml) · GPL-3.0 licensed · Windows 10/11

> **Work in progress (v0.1).** It already works well and runs daily on the author's PC with a very small footprint, but it's early: the installer is
> unsigned (see [Code signing policy](#code-signing-policy)), there's no auto-update yet, and some paths are still being verified on more hardware (see [Status](#status)).

## At a glance

| | |
|---|---|
| **Extra delay on a lookup** | ≈ **0.5 ms** (median 3.1 ms through Hushwire vs 2.4 ms asking the upstream DNS directly) |
| Blocked / cached lookup | **0.04 ms** / **0.05 ms** |
| **CPU while idle** | **≈ 0.1 % of one core** |
| **Memory with 1.1 million rules** | **≈ 160 MB** (≈ 94 MB private), window closed to the tray |
| Start-up to first answer | ≈ 2 s with all 28 lists |
| Throughput | ≈ 58,000 blocked / 83,000 cached lookups per second (a PC makes well under 100) |
| First real use | 12,706 lookups, **50 % blocked**, 0 upstream failures |
| Installer | **0.2 MB** (needs the .NET 9 Desktop Runtime) or 53 MB (self-contained) |

Method, caveats and how to reproduce: **[docs/PERFORMANCE.md](docs/PERFORMANCE.md)**.

## Screenshots

| | |
|---|---|
| ![Dashboard](docs/screenshots/dashboard.png) | ![Activity log](docs/screenshots/activity.png) |
| **Dashboard**: lookups and blocks today and all time, by category, by list, top domains and the app that asked | **Activity log**: every blocked lookup with the rule and list that caught it; allow or block a domain in one click |
| ![Filter lists](docs/screenshots/lists.png) | ![Allow / Block](docs/screenshots/rules.png) |
| **Filter lists**: 28 built-in lists, your own `https://` lists, safe atomic updates | **Allow / Block**: your own rules beat every list, plus a "why is this blocked?" check |

*Captured in developer mode (`--dev --snapshot`) with sample lookups sent to the local proxy, so the counts are small and the asking app is the test script.*

---

## What it does

- **Blocks at the DNS level, for the whole PC.** A local DNS proxy listens on `127.0.0.1` and `::1` (UDP and TCP, IPv4 and IPv6). Blocked names get
  `0.0.0.0` / `::`, so connections fail instantly; everything else goes to the DNS servers you already use (or ones you choose) and is cached.
- **28 built-in filter lists** with descriptions and licenses — StevenBlack, EasyPrivacy and URLhaus on by default; HaGeZi, OISD, AdAway, Disconnect,
  phishing/scam/malware, CNAME-hidden "first-party" trackers, smart-TV and Windows-telemetry lists one click away. Add your own `https://` lists too
  (hosts files, domain lists, Adblock-style DNS rules).
- **Safe list updates.** HTTPS only, size limit, not HTML, no binary data, sane rule ratio, no sudden collapse in size; swapped in atomically, previous copy kept.
  A bad download never replaces a working list.
- **Your rules win.** Personal allow/block lists override every list; a more specific rule beats a broader one. A "why is this blocked?" check shows the exact
  rule and list.
- **Catches hidden trackers.** CNAME-cloaking detection blocks a tracker hiding behind a site's own subdomain.
- **Won't break Windows.** Protected domains (Windows Update, Defender, SmartScreen, connectivity checks…) can't be blocked by any list.
- **Dashboard.** Lookups, blocked, allowed and cache hits (today and all time), blocked by category and list, top blocked domains, which application asked,
  activity log, per-application exemptions, pause for 5 min / 30 min / 1 h, tray icon with status colour, built-in diagnostics.
- **Fails safe.** Your original DNS settings are saved before anything changes and restored on turn-off, exit, crash (a watchdog process) and reboot
  (a boot-time safety task). The Start-menu shortcut *Hushwire – repair network settings* works even if the app itself is broken.
- **Optional in-page YouTube & Twitch ad filter** — off by default, [see below](#youtube--twitch-ads-inside-the-page-optional-off-by-default).

## What it can't do

DNS filtering has real limits; Hushwire doesn't pretend otherwise.

- It **can't remove ads served from the same hostname as the content** (most of YouTube, many news sites), hide empty ad frames, or count or delete cookies.
  The dashboard counts **blocked DNS lookups, not ads or cookies**; one page can trigger dozens of lookups.
- Browsers or apps with their **own encrypted DNS** ("Secure DNS" / DNS over HTTPS), hard-coded DNS servers, or a **VPN's DNS** bypass it. Diagnostics shows which adapters are covered.
- It **can't always tell which program asked**: most Windows apps go through the shared DNS Client service and appear as "Windows DNS Client". Apps with their own resolver
  (most browsers, many games) are identified exactly.

---

## Install

Requirements: **Windows 10 (2004+) or 11, 64-bit.** Build the installers with `installer\build-installer.ps1` (see [Development](#development)):

| File | Size | Notes |
|---|---|---|
| `dist\Hushwire-Setup.msi` | **0.2 MB** | Needs the **.NET 9 Desktop Runtime** (shared with other apps, updated by Windows Update); Windows offers a download link if it's missing |
| `dist\Hushwire-Setup-standalone.msi` | **53 MB** (155 MB installed) | Carries its own runtime, for a PC without .NET 9 |
| `dist\Hushwire-Portable.exe` | **0.6 MB** | No install: just run it (needs the .NET 9 Desktop Runtime) |
| `dist\Hushwire-Portable-standalone.exe` | **163 MB** | No install, carries its own runtime |

The portable exes need no installer and leave nothing behind until you click **Turn on**. When protection or start-with-Windows is enabled they copy themselves to `C:\Program Files\Hushwire`, so the elevated scheduled tasks never point at a folder a normal user can write to. Use the by-hand uninstall steps below to remove them.

The installer puts Hushwire in `C:\Program Files\Hushwire` (`Hushwire.exe`, with the app's assemblies packed inside it, plus the license and third-party notices), adds Start-menu entries, and starts Hushwire when it finishes.
A newer version installs over an older one and keeps your settings and lists; a running copy is closed cleanly first. **Uninstall from Windows Settings → Apps**: it restores DNS, removes the
scheduled tasks and the YouTube/Twitch certificate, and deletes Hushwire's data. The installer is unsigned for now, so SmartScreen may warn on first run.

1. Install (or run `Hushwire.exe`). Windows asks for administrator rights — Hushwire needs them to listen on port 53 and change adapter DNS.
2. Click **Turn on**. The first time it downloads the default lists, then points your network adapters at itself.
3. Check **About & diagnostics → Run checks**: green ticks mean the proxy is listening, your adapter uses Hushwire, normal sites resolve and a known ad domain is blocked.

Closing the window sends Hushwire to the tray and protection keeps running. **Exit** in the tray menu turns protection off and restores your DNS (protection returns the next time Hushwire starts).
*Settings → Start Hushwire when I sign in* starts it with Windows.

### What Hushwire changes on your PC
| Change | Where | When |
|---|---|---|
| Adapter DNS servers → `127.0.0.1` / `::1` | Physical Ethernet/Wi-Fi adapters only (VPN, virtual and filter-driver adapters are left alone) | While protection is on; originals saved and restored |
| Data, settings, lists, journal of the original DNS | `%ProgramData%\Hushwire` | Always |
| Scheduled tasks `HushwireRecover` (boot-time DNS safety net) and `HushwireApp` (start at sign-in, optional) | Task Scheduler | While protection is on / if enabled |
| A private certificate authority | `LocalMachine\Root`; keys in `%ProgramData%\Hushwire\ca` | **Only** if you turn on the YouTube/Twitch filter |

Nothing else is touched: no firewall rules, proxy settings, hosts file or drivers.

<details>
<summary>Uninstall by hand (if you ran the plain exe instead of the installer)</summary>

1. In Hushwire: **Settings → Turn off and remove the certificate** (if you ever used the YouTube/Twitch filter), then tray → **Exit**.
2. Check DNS is back: `Get-DnsClientServerAddress` should show your normal servers, not `127.0.0.1`. If not, run `Hushwire.exe --restore`.
3. Remove the tasks (admin PowerShell): `Unregister-ScheduledTask -TaskName HushwireRecover,HushwireApp -Confirm:$false`
4. Delete `%ProgramFiles%\Hushwire`, `%ProgramData%\Hushwire` and the folder you ran Hushwire from.
</details>

---

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

## Performance

| | Result |
|---|---|
| Lookup through Hushwire, first time (forwarded) | median **3.1 ms** vs 2.4 ms asking the upstream directly (≈ +0.5 ms) |
| Lookup Hushwire has cached / lookup of a blocked name | **0.05 ms** / **0.04 ms** |
| Idle CPU (tray, 1.1 M rules) | **0.06 s of CPU per minute** |
| Memory (1.1 M rules, window closed) | ≈ **160 MB** in use, ≈ **94 MB** private (the .NET/WPF runtime is most of it; the rules are ~36 MB) |
| Build the filter from all 28 lists | ≈ **1 s** |
| One filter lookup | **~135–200 ns**, no allocation |
| Add or remove a personal rule | instant |
| Proxy throughput, 32 clients | 58 k blocked/s · 83 k cached/s · 7.8 k forwarded/s |

Measuring found real problems, which are fixed: the first version used **828 MB** and froze for seconds on Exit (now ~160 MB and instant), and **1 in ~100 forwarded lookups stalled for 2 s** because
Hushwire didn't re-send dropped UDP packets (the slowest 1 % of lookups went from 2,000 ms to ~410 ms, and every lookup now gets an answer). Full tables, test machine, caveats and the commands to reproduce
everything: **[docs/PERFORMANCE.md](docs/PERFORMANCE.md)**.

## Privacy and safety

- **This program will not transfer any information to other networked systems unless specifically requested by the user.** There is no telemetry, no account, no cloud and no update check. The only network connections Hushwire makes are DNS to your chosen upstream and downloads from the list URLs you enabled. Statistics and the activity log stay on this PC
  (`%ProgramData%\Hushwire`, administrators only). The log records blocked lookups (allowed ones only if you switch that on); clear it any time.
- List downloads require HTTPS and normal certificate validation; redirects to non-HTTPS are refused. Lists are parsed as data only. Settings and imports are validated; malformed values are dropped rather than trusted.
- The DNS safety net (journal, watchdog, boot task, repair shortcut, `--restore`) exists because pointing DNS at a program that isn't running would break the internet.

---

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

## Code signing policy

Releases are **not signed yet**: the project is applying for free code signing from the [SignPath Foundation](https://signpath.org/). Until then SmartScreen will warn when you run the installer;
check the SHA-256 on the release page. The policy (what gets signed, the roles, the rules) is in [docs/CODE_SIGNING.md](docs/CODE_SIGNING.md). Installers are built from source by a
[GitHub Actions workflow](.github/workflows/build.yml).

| Role | Who |
|---|---|
| Committer, reviewer and approver | [gbogdev](https://github.com/gbogdev) |

## License

Hushwire is free software, licensed under the **[GNU General Public License v3.0](LICENSE)**: you may use, study, share and modify it, and if you distribute modified versions you must do so under the same
license and make the source available. Copyright (C) 2026 gbogdev. Third-party components and filter lists keep their own licenses: see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
Security problems: see [SECURITY.md](SECURITY.md).

## Credits

Some features were built with the help of AI tools. It is provided as is: review it and test it before you rely on it.
