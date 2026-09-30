# Builds the Windows sing-box daemon (boxdd) from the pinned upstream tree and
# signs it with the shared development certificate (same signer as the app —
# required by boxdd peer authentication).
[CmdletBinding()]
param(
    [string] $SingBoxSource,
    [ValidateSet("x64", "arm64")] [string] $Architecture = "x64",
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot is empty in parameter defaults on Windows PowerShell 5.1 when
# [CmdletBinding()] is present, so resolve defaults here instead.
if (-not $SingBoxSource) { $SingBoxSource = Join-Path (Split-Path $PSScriptRoot -Parent) ".cache\upstream\sing-box" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "..\daemon" }
$goExe = (Get-Command go -ErrorAction SilentlyContinue).Source
if (-not $goExe) {
    $candidate = 'C:\Program Files\Go\bin\go.exe'
    if (Test-Path $candidate) { $goExe = $candidate } else { throw 'Go was not found. Install GoLang.Go and reopen the shell.' }
}

$source = Resolve-Path $SingBoxSource -ErrorAction Stop
if (-not (Test-Path (Join-Path $source "go.mod"))) {
    throw "SingBoxSource must point to a checked-out sing-box source tree."
}

& (Join-Path $PSScriptRoot 'Apply-DaemonPatches.ps1') -SingBoxSource $source

# Use the same Go toolchain and build entry point as the pinned official client.
$lock = Get-Content (Join-Path $PSScriptRoot 'sources.lock.json') -Raw | ConvertFrom-Json
$env:GOTOOLCHAIN = $lock.toolchain.go
$toolchainRoot = (& $goExe env GOROOT).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to provision the pinned Go toolchain.' }
$goExe = Join-Path $toolchainRoot 'bin\go.exe'
$env:PATH = "$(Split-Path $goExe -Parent);$env:PATH"
$actualGo = (& $goExe env GOVERSION).Trim()
if ($actualGo -ne $lock.toolchain.go) { throw "Expected $($lock.toolchain.go), found $actualGo." }

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$cacheRoot = Join-Path $PSScriptRoot "..\.cache"
$env:CGO_ENABLED = '0'
$env:GOOS = 'windows'
$env:GOARCH = if ($Architecture -eq 'arm64') { 'arm64' } else { 'amd64' }
$env:GOCACHE = Join-Path $cacheRoot 'gocache'
$env:GOMODCACHE = Join-Path $cacheRoot 'gomod'
$env:GOPATH = Join-Path $cacheRoot 'gopath'

$daemonExe = [IO.Path]::GetFullPath((Join-Path $OutputDirectory 'sing-box-daemon.exe'))
Push-Location $source
try {
    & $goExe run ./cmd/internal/build_boxdd "-target=windows/$($env:GOARCH)" "-output=$daemonExe"
}
finally {
    Pop-Location
}
if ($LASTEXITCODE -ne 0) { throw "boxdd build failed" }

& (Join-Path $PSScriptRoot "Sign-Outputs.ps1") -Paths $daemonExe

Write-Host "Built and signed Windows sing-box daemon: $daemonExe"
