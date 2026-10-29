# Security policy

Hushwire changes your network's DNS settings and, if you opt in, installs a name-constrained certificate, so security reports are taken seriously.

## Reporting a vulnerability
Please **don't open a public issue** for a security problem. Use GitHub's private reporting instead:
**Security tab → "Report a vulnerability"** on this repository. Include what you found, how to reproduce it, and the Hushwire version (*About & diagnostics*).

This is a one-person project in early development, so there is no guaranteed response time, but reports are read and fixed as quickly as practical.

## Supported versions
Only the latest release receives fixes.

## What is in scope
- Anything that lets a non-administrator change DNS settings, rules, lists or the certificate through Hushwire
- The local YouTube/Twitch filter (certificate handling, the loopback HTTPS server, anything that lets a site other than the intended ones be intercepted)
- Parsing of filter lists, imports or network data (crashes, memory use, code execution)
- Installer / uninstaller behaviour that leaves the machine in a bad state
