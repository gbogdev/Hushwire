Set-Location (Split-Path $PSScriptRoot -Parent)

# dist\Hushwire.exe: tiny (~0.5 MB). Needs the .NET 9 Desktop Runtime, which is shared with other apps and updated by Windows Update.
dotnet publish src/Hushwire.App -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -o dist
if ($LASTEXITCODE -ne 0) { throw "publish of dist\Hushwire.exe failed" }

# dist\standalone\Hushwire.exe: carries its own runtime (~155 MB), for PCs without .NET 9. Uncompressed on purpose:
# single-file compression unpacks ~100 MB of assemblies into RAM at every start.
dotnet publish src/Hushwire.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\standalone
if ($LASTEXITCODE -ne 0) { throw "publish of dist\standalone\Hushwire.exe failed" }

# Portable downloads: the same exes under release names (no installer; run from anywhere)
Copy-Item dist\Hushwire.exe dist\Hushwire-Portable.exe -Force
Copy-Item dist\standalone\Hushwire.exe dist\Hushwire-Portable-standalone.exe -Force

Write-Host "dist\Hushwire.exe (needs .NET 9 Desktop Runtime) and dist\standalone\Hushwire.exe (self-contained) ready"
