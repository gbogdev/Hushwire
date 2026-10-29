# Code signing policy

**Status:** releases are **not signed yet**. Hushwire is applying for free code signing from the
[SignPath Foundation](https://signpath.org/). Until that is approved, installers are unsigned and Windows SmartScreen will warn
before running them; compare the SHA-256 in `SHA256SUMS.txt` on the release page to confirm your download is intact.

Once signing is approved, signed releases will carry this attribution: *"Free code signing provided by SignPath.io, certificate by SignPath Foundation."*

## What is signed
Only binaries built from this repository's own source by the [GitHub Actions workflow](../.github/workflows/build.yml):
`Hushwire.exe` and the `.msi` installers (`Hushwire-Setup.msi`, `Hushwire-Setup-standalone.msi`). Product name is `Hushwire` and every build of a release carries the same version.
Build scripts, the installer definition and the workflow are part of the source and are reviewed like any other code.

## Roles
| Role | Who |
|---|---|
| Author / committer | [gbogdev](https://github.com/gbogdev) |
| Reviewer (reviews changes from non-committers) | [gbogdev](https://github.com/gbogdev) |
| Approver (approves every signing request, manually, per release) | [gbogdev](https://github.com/gbogdev) |

The project currently has a single maintainer. Anyone with these roles uses multi-factor authentication for GitHub and for SignPath.

## Rules
- Every release is built from a tagged commit in this repository and is approved manually before it is signed.
- Hushwire contains no malware, no potentially unwanted software and no tools that scan for or exploit vulnerabilities in other systems.
- Hushwire changes system configuration only when the user clicks **Turn on** (DNS settings) or explicitly opts in to the YouTube/Twitch filter (a name-constrained certificate), tells the user what it will change first,
  and the installer has a matching uninstall that restores everything (see the README's "What Hushwire changes on your PC").

## Privacy
This program will not transfer any information to other networked systems unless specifically requested by the user. The only connections it makes are DNS queries to the
upstream servers the user's PC already used (or chose in Settings) and downloads from the filter-list URLs the user enabled. There is no telemetry, no account and no update check.
See the README's "Privacy and safety" section.
