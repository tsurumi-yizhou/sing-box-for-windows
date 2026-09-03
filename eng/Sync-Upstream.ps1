# Populates and maintains the pinned upstream checkouts under eng\upstream\.
#
# The lock file (eng\upstream\sources.lock.json) is the single source of truth for
# which upstream revisions this repository is built against. This script can:
#
#   -Check          Verify every checkout matches its pin (offline, read-only).
#   (default)       Clone missing checkouts and checkout the pinned commits.
#   -Pin name=sha   Advance a pin to a new commit and rewrite the lock file.
#
# Adding a new pinned source (for example sing-box-for-desktop) is a lock-file
# edit: add an entry whose Name matches the SagerNet repository name, then run
# this script. Entries are cloned from https://github.com/SagerNet/<Name>.git.
[CmdletBinding()]
param(
    # Verify mode: report drift between checkouts and the lock file, change
    # nothing, and exit non-zero on any mismatch. Works offline (no fetch).
    [switch] $Check,

    # Advance pins: -Pin singbox=<full-commit-sha> [-Pin android=<sha> ...]
    [string[]] $Pin,

    # Repository host template. {0} is replaced with the entry Name.
    [string] $UrlTemplate = "https://github.com/SagerNet/{0}.git",

    [string] $UpstreamDirectory
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot is empty in parameter defaults on Windows PowerShell 5.1 when
# [CmdletBinding()] is present, so resolve defaults here instead.
if (-not $UpstreamDirectory) { $UpstreamDirectory = Join-Path $PSScriptRoot "upstream" }
$upstream = [System.IO.Path]::GetFullPath($UpstreamDirectory)
$lockPath = Join-Path $upstream "sources.lock.json"

if (-not (Test-Path $lockPath)) {
    throw "Lock file not found: $lockPath"
}
$lock = Get-Content $lockPath -Raw | ConvertFrom-Json

# Well-known lock entries: lock property name -> default repository name.
# Extra entries present in the lock file (e.g. a future "desktop") are handled
# automatically; this table only provides ordering and defaults.
$knownOrder = @(
    [pscustomobject]@{ Key = "singbox"; FallbackName = "sing-box" },
    [pscustomobject]@{ Key = "apple";   FallbackName = "sing-box-for-apple" },
    [pscustomobject]@{ Key = "android"; FallbackName = "sing-box-for-android" },
    [pscustomobject]@{ Key = "desktop"; FallbackName = "sing-box-for-desktop" }
)

$entries = @()
foreach ($known in $knownOrder) {
    $entry = $lock.PSObject.Properties[$known.Key]
    if ($null -ne $entry) {
        $entries += [pscustomobject]@{ Key = $known.Key; Name = $entry.Value.Name; Commit = $entry.Value.Commit }
    }
}
foreach ($prop in $lock.PSObject.Properties) {
    if ($prop.Name -in @("generatedAt", "toolchain") + $knownOrder.Key) { continue }
    if ($prop.Value.PSObject.Properties["Commit"]) {
        $entries += [pscustomobject]@{ Key = $prop.Name; Name = $prop.Value.Name; Commit = $prop.Value.Commit }
    }
}
if ($entries.Count -eq 0) { throw "No pinned sources found in $lockPath" }

function Get-HeadCommit([string] $path) {
    $commit = git -C $path rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return $commit.Trim()
}

$failures = @()

foreach ($entry in $entries) {
    $path = Join-Path $upstream $entry.Name
    $label = "$($entry.Key) ($($entry.Name))"

    if (-not (Test-Path (Join-Path $path ".git"))) {
        if ($Check) {
            $failures += "$label : missing checkout at $path"
            continue
        }
        Write-Host "Cloning $label ..."
        git clone ( $UrlTemplate -f $entry.Name ) $path
        if ($LASTEXITCODE -ne 0) { throw "git clone failed for $label" }
    }

    $head = Get-HeadCommit $path
    if ($head -ne $entry.Commit) {
        if ($Check) {
            $failures += "$label : checkout at $head, locked to $($entry.Commit)"
            continue
        }
        Write-Host "Checking out $label at $($entry.Commit) ..."
        git -C $path fetch --all --tags --quiet
        git -C $path checkout --detach $entry.Commit
        if ($LASTEXITCODE -ne 0) { throw "git checkout failed for $label" }
        $head = Get-HeadCommit $path
        if ($head -ne $entry.Commit) { throw "Failed to pin $label to $($entry.Commit)" }
    }
    Write-Host "$label pinned at $($entry.Commit.Substring(0, [Math]::Min(8, $entry.Commit.Length)))"
}

# Verify the gRPC contract mirror: Protos\*.proto must match the pinned
# sing-box tree (the desktop_service import path is rewritten for the flat
# ProtoRoot layout; that single line is the sanctioned difference).
$singBoxEntry = $entries | Where-Object { $_.Key -eq "singbox" } | Select-Object -First 1
if ($null -ne $singBoxEntry) {
    $singBoxPath = Join-Path $upstream $singBoxEntry.Name
    $protoPairs = @(
        @("started_service.proto", "daemon\started_service.proto"),
        @("managed_service.proto", "daemon\managed_service.proto"),
        @("desktop_service.proto", "experimental\boxdd\desktop_service.proto")
    )
    foreach ($pair in $protoPairs) {
        $localPath = Join-Path $PSScriptRoot ("..\Protos\" + $pair[0])
        $upstreamPath = Join-Path $singBoxPath $pair[1]
        if (-not (Test-Path $localPath) -or -not (Test-Path $upstreamPath)) { continue }
        $localLines = Get-Content $localPath
        $upstreamLines = Get-Content $upstreamPath
        $diff = Compare-Object $localLines $upstreamLines -CaseSensitive |
            Where-Object { $_.InputObject -notmatch '^\s*import ".*started_service\.proto";' }
        if ($diff) {
            $failures += "Protos\$($pair[0]) drifted from upstream $($pair[1])"
        }
    }
}

if ($Check) {
    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Error $_ }
        exit 1
    }
    Write-Host "All upstream checkouts match the lock file; protos are in sync."
    exit 0
}

# Advance pins when requested: update the lock entry to the new commit and
# record the commit date, then check out the new pin.
foreach ($pinSpec in ($Pin | Where-Object { $_ })) {
    if ($pinSpec -notmatch '^([^=]+)=([0-9a-fA-F]{40})$') {
        throw "Invalid -Pin value '$pinSpec'; expected <key>=<full-commit-sha>."
    }
    $key = $Matches[1]; $commit = $Matches[2].ToLowerInvariant()
    $prop = $lock.PSObject.Properties[$key]
    if ($null -eq $prop) { throw "No lock entry named '$key'." }
    $path = Join-Path $upstream $prop.Value.Name
    if (-not (Test-Path (Join-Path $path ".git"))) { throw "Checkout for '$key' is missing; run without -Pin first." }
    git -C $path fetch --all --tags --quiet
    $date = git -C $path show -s --format='%cI' $commit
    if ($LASTEXITCODE -ne 0) { throw "Commit $commit not found in $($prop.Value.Name)." }
    $prop.Value.Ref = $commit
    $prop.Value.Commit = $commit
    $prop.Value | Add-Member -NotePropertyName CommitDate -NotePropertyValue $date -Force
    git -C $path checkout --detach $commit
    if ($LASTEXITCODE -ne 0) { throw "git checkout failed for '$key'." }
    Write-Host "$key pinned to $commit ($date)"
}

if ($Pin) {
    $lock.generatedAt = (Get-Date).ToUniversalTime().ToString("o")
    $lock | ConvertTo-Json -Depth 8 | Set-Content $lockPath -Encoding utf8
    Write-Host "Lock file updated: $lockPath"
    Write-Host "Re-run this script with -Check to verify the new state, then rebuild the daemon: eng\build-daemon.ps1"
}
