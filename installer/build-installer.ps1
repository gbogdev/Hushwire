# Builds dist\Hushwire-Setup.msi (small; needs the .NET 9 Desktop Runtime) or, with -Standalone, dist\Hushwire-Setup-standalone.msi
# (carries its own runtime). Publishes the app first if dist\ is missing.
param([switch]$Standalone)
Set-Location (Split-Path $PSScriptRoot -Parent)
$ErrorActionPreference = 'Stop'

$version = ([xml](Get-Content src\Hushwire.App\Hushwire.App.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$source  = if ($Standalone) { 'dist\standalone' } else { 'dist' }
$output  = if ($Standalone) { 'dist\Hushwire-Setup-standalone.msi' } else { 'dist\Hushwire-Setup.msi' }

if (-not (Test-Path "$source\Hushwire.exe")) { & .\scripts\build-release.ps1 }
# never package a stale exe left over from an earlier version or a failed publish
$built = (Get-Item "$source\Hushwire.exe").VersionInfo.ProductVersion
if (-not $built.StartsWith($version)) { throw "$source\Hushwire.exe is version $built but the project is ${version}. Run scripts\build-release.ps1 first" }
dotnet tool restore | Out-Null
dotnet wix build installer\Hushwire.wxs -arch x64 -d Version=$version -d SourceDir=$source -d RepoRoot=. -o $output
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }
# ICE38/43/57 only complain that Start-menu shortcuts of a per-machine package live under a user-profile folder; with ALLUSERS=1 (set by
# Scope=perMachine) it is the shared Start menu, so they are suppressed. Everything else must pass.
dotnet wix msi validate -sice ICE38 -sice ICE43 -sice ICE57 $output
if ($LASTEXITCODE -ne 0) { throw 'installer validation failed' }
Write-Host ("{0} ready ({1:N1} MB, version {2})" -f $output, ((Get-Item $output).Length / 1MB), $version)
