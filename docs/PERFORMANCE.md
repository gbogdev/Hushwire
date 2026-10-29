# Hushwire performance and early results

> **Work in progress (v0.1).** Hushwire is an early build and still changing, but it works well so far and its footprint is
> small. These are measurements from one machine, taken with the scripts in this repository, so you can re-run them. They are not
> marketing numbers: where something was wrong, this page says so.

**Test machine:** AMD Ryzen 5 5600 (6 cores / 12 threads), 32 GB RAM, Windows 11 Pro (build 26300), .NET 9.0.
Upstream DNS was the ISP's resolver on the local network. All 28 filter lists enabled (≈ **1.1 million unique rules**) unless noted.

## The short version

| | Result |
|---|---|
| Extra delay on a normal lookup | about **0.5 ms** (median 3.1 ms through Hushwire vs 2.4 ms asking the upstream directly) |
| Blocked lookup | answered in **0.04 ms** (nothing is sent anywhere) |
| Repeat lookup (Hushwire's cache) | **0.05 ms** |
| CPU while idle (tray, all 28 lists) | **0.1 % of one core** (0.06 s of CPU per minute) |
| Memory, 1.1 M rules loaded | **~160 MB in use / ~94 MB private** (window closed to the tray) |
| Start-up to first answer, 1.1 M rules | **~2.2 s** |
| Throughput | ~**58,000 blocked** / ~**83,000 cached** lookups per second (a PC normally makes well under 100) |
| Real use so far | **12,706 lookups** in the first session, **50 %** blocked, 0 upstream failures |

## 1. Latency: does Hushwire slow the internet down?

One client, one lookup at a time (what a program actually experiences), 144 popular names (`google.com`, `www.google.com`, …) and 150 real ad/tracker
names, against the real upstream DNS. Three runs of `dotnet run -c Release --project bench/Hushwire.Bench -- latency <upstream-ip>`:

| | median | 95th percentile | 99th percentile |
|---|---|---|---|
| Asking the upstream DNS directly | 2.0 – 2.8 ms | 5 – 33 ms | 21 – 91 ms |
| **Through Hushwire**, first time (forwarded) | 3.1 – 3.5 ms | 6 – 31 ms | ~410 ms (see below) |
| **Through Hushwire**, repeat (cached) | **0.05 ms** | 0.1 – 3 ms | 0.1 – 36 ms |
| **Through Hushwire**, ad/tracker name (blocked) | **0.04 ms** | 0.1 ms | 0.4 – 0.6 ms |

- A forwarded lookup costs about half a millisecond more than asking directly (an extra local hop and a fresh socket per query).
- Anything Hushwire has seen recently comes back in about 50 microseconds, which is faster than the network can do it, so for repeated names Hushwire is a net speed-up.
- Blocked names never leave the PC: instant failure instead of a connection attempt and a timeout.

### A real problem this measurement found (and fixed)
The first run showed **1 in ~100 forwarded lookups taking exactly 2 seconds**. The ISP resolver drops the occasional UDP packet (asking it directly, 1–2 % of queries
never got an answer at all), and Hushwire waited a single 2 s timeout and then gave up instead of resending. Hushwire now retransmits an unanswered query after 400 ms, then 600 ms, then 1 s
(the way real resolvers do). Result over three runs: **99th percentile 2,000 ms → ~410 ms, and every lookup got an answer** (144 of 144, where the direct baseline lost 1–6 of 288).

## 2. Resource use

Measured on the shipped small build with all 28 lists on, running in the tray, 60 seconds of idle:

| | |
|---|---|
| Idle CPU | **0.062 s of CPU in 60 s** (0.10 % of one core) |
| Memory | 162 MB working set, **94 MB private** (the .NET and WPF runtime is most of it; the rules themselves are ~36 MB) |
| Handles / threads | 490 / 25 |
| Start-up CPU | 1.7 s once at launch (loading and indexing 1.1 M rules) |

Optimization history (same machine): the first version used **828 MB** and ~2,300 handles with these lists and froze for seconds on Exit; it now uses ~160 MB, ~490
handles, and exits instantly. Rules moved from strings to 64-bit hashes (about 34 bytes per rule instead of 100+), lists are parsed from raw bytes, the UI uses software
rendering and redraws only when something changed, and memory is trimmed after the window is closed.

## 3. Engine micro-benchmarks

`dotnet run -c Release --project bench/Hushwire.Bench -- all` (set `HUSHWIRE_BENCH_FULL=1` for every installed list):

| | |
|---|---|
| Build the filter from 6 lists (537 k rules) | 513 ms, 21 MB kept |
| Build from all 28 lists (1.12 M unique rules, 1.83 M lines) | ~1.0 s, 36 MB kept |
| One lookup, name is blocked | ~200 ns, no allocation |
| One lookup, name is allowed | ~135 ns, no allocation |
| Validate a 4.5 MB list download | 54 ms |
| Add or remove one personal allow/block rule | instant (the lists are not re-read) |
| DNS proxy with 32 concurrent clients | 58 k blocked/s, 83 k cached/s, 7.8 k forwarded/s (one new socket per forwarded query) |

## 4. What it has blocked so far

From the installed app's own statistics (aggregate counts only), covering the first hours of use and testing on one PC with 28 lists on. These count
**DNS lookups**, not ads, banners or cookies, and they include some deliberate test lookups and apps retrying blocked names.

| | |
|---|---|
| Lookups | 12,706 |
| Blocked | 6,362 (**50.1 %**) |
| Answered from cache | 1,445 (11.4 %) |
| Failed upstream | 0 |
| Of the blocked: ads / Windows telemetry / trackers | 6,206 / 112 / 44 |
| Blocked because a CNAME alias pointed at a blocked tracker | 2,166 |
| Biggest contributors | StevenBlack 4,349 · AdGuard DNS filter 1,467 · OISD big 204 · HaGeZi Pro 158 |

Half of all lookups being blocked is high because a lot of that traffic is repeated retries of the same tracker names and because many lists overlap (a name is credited to
the first list that matches). It is a measure of *activity*, not of how many ads you would have seen.

## Limits of these numbers
- One PC, one network, one upstream resolver. Latency in particular depends on your upstream.
- The latency test uses well-known names, which the upstream resolver usually has cached, so "forwarded" numbers are the optimistic case; cold names that need full recursion are slower with or without Hushwire.
- Stats are from a single short session and include test traffic; they will be replaced with longer-running data.
- Not yet measured: VPN, sleep/resume, Windows 10, heavy multi-user or server loads.

## Reproduce
```
dotnet run -c Release --project bench/Hushwire.Bench -- all                       # engine and proxy throughput
dotnet run -c Release --project bench/Hushwire.Bench -- latency <upstream dns ip> # latency, direct vs through Hushwire
HUSHWIRE_BENCH_FULL=1 dotnet run -c Release --project bench/Hushwire.Bench -- parse  # all installed lists
```
Idle and memory figures: start Hushwire with `--dev --tray` and read the process in Task Manager or `Get-Process`.
