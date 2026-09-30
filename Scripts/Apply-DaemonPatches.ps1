# Apply repository-owned fixes to the pinned upstream checkout. Fail closed
# on upstream drift; never reset or overwrite an incompatible local change.
[CmdletBinding()]
param([Parameter(Mandatory = $true)] [string] $SingBoxSource)

$ErrorActionPreference = 'Stop'
$sourcePath = (Resolve-Path -LiteralPath $SingBoxSource).Path
$patchDirectory = Join-Path $PSScriptRoot 'daemon-patches'
foreach ($patch in (Get-ChildItem -LiteralPath $patchDirectory -Filter '*.patch' | Sort-Object Name)) {
    & git -C $sourcePath apply --reverse --check -- $patch.FullName 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Already applied: $($patch.Name)"
        continue
    }
    & git -C $sourcePath apply --check -- $patch.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot apply $($patch.Name): upstream drift or conflicting local edits."
    }
    & git -C $sourcePath apply -- $patch.FullName
    if ($LASTEXITCODE -ne 0) { throw "Failed to apply $($patch.Name)." }
    Write-Host "Applied: $($patch.Name)"
}

# Do not leak a failed reverse-check exit code to callers.
$global:LASTEXITCODE = 0
