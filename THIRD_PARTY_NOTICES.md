# Third-party notices

Hushwire itself is licensed under the GNU General Public License v3.0 (see [LICENSE](LICENSE)). It builds on the following, all unmodified and all under licenses compatible with the GPL-3.0.

## Shipped with the application
| Component | License | Notes |
|---|---|---|
| .NET 9 runtime and base class libraries | MIT | Microsoft. Loaded from the installed .NET Desktop Runtime, or bundled in the *standalone* installer |
| Windows Presentation Foundation (WPF) and Windows Forms (tray icon) | MIT | Microsoft; part of the .NET Desktop Runtime |
| Windows system libraries (`iphlpapi.dll`, `dnsapi.dll`, `kernel32.dll`, …) | Windows | Called through P/Invoke; not redistributed |

Hushwire has **no NuGet package dependencies** in the shipped application.

## Build tools (not shipped)
| Tool | License |
|---|---|
| WiX Toolset 5.0.2 (builds the MSI installer) | Microsoft Reciprocal License (MS-RL) |

## Filter lists
Hushwire downloads filter lists from their publishers at run time and does **not** redistribute them; nothing from a list is included in this repository or in the installers.
Each list's license is shown next to it in the app (*Filter lists*) and comes from the publisher. Some are GPL, CC BY-SA or CC BY-NC (for example Phishing Army is
non-commercial); by turning a list on you use it under its publisher's terms.
