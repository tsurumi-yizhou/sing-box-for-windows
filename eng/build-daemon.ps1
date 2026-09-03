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
if (-not $SingBoxSource) { $SingBoxSource = Join-Path $PSScriptRoot "upstream\sing-box" }
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

# The daemon is built from a pinned upstream revision; the pinned toolchain
# keeps builds reproducible across machines (mirrors the official desktop
# client's version.json, which tracks sing-box version + Go version).
$lockPath = Join-Path $PSScriptRoot "upstream\sources.lock.json"
if (Test-Path $lockPath) {
    $lockedGo = (Get-Content $lockPath -Raw | ConvertFrom-Json).toolchain.go
    $actualGo = (& $goExe version) -replace '^go version (\S+) .*$', '$1'
    if ($lockedGo -and $actualGo -ne $lockedGo) {
        Write-Warning "Go toolchain mismatch: lock file pins $lockedGo, found $actualGo. " +
            "Update the pin in eng\upstream\sources.lock.json if the upgrade is intentional."
    }
}

$commit = (git -C $source rev-parse --short HEAD 2>$null)
$singBoxVersion = if ($commit) { "sing-box@$commit" } else { "sing-box (pinned revision)" }

# Official Windows release tags, minus `tfogo_checklinkname0` (tfo-go's
# //go:linkname tricks break on newer Go toolchains; the official stub fallback
# is used instead, disabling TCP Fast Open on Windows only).
$tagsFile = Join-Path $source "release\DEFAULT_BUILD_TAGS_WINDOWS"
$tags = if (Test-Path $tagsFile) {
    ((Get-Content $tagsFile -Raw).Trim() -split ',' | Where-Object { $_ -ne 'tfogo_checklinkname0' }) -join ','
} else { "" }
$tagsArg = if ($tags) { @('-tags', $tags) } else { @() }

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$cacheRoot = Join-Path $PSScriptRoot "..\.cache"
$env:CGO_ENABLED = '0'
$env:GOOS = 'windows'
$env:GOARCH = if ($Architecture -eq 'arm64') { 'arm64' } else { 'amd64' }
$env:GOCACHE = Join-Path $cacheRoot 'gocache'
$env:GOMODCACHE = Join-Path $cacheRoot 'gomod'
$env:GOPATH = Join-Path $cacheRoot 'gopath'

$daemonExe = Join-Path $OutputDirectory 'sing-box-daemon.exe'
Push-Location $source
try {
    & $goExe build -ldflags "-H windowsgui -X github.com/sagernet/sing-box/constant.Version=$singBoxVersion" @tagsArg -o $daemonExe ./experimental/boxdd
}
finally {
    Pop-Location
}
if ($LASTEXITCODE -ne 0) { throw "boxdd build failed" }

& (Join-Path $PSScriptRoot "Sign-Outputs.ps1") -Paths $daemonExe

Write-Host "Built and signed Windows sing-box daemon: $daemonExe"
