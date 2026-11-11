# Apply repository-owned fixes to the pinned upstream checkout. Fail closed
# on upstream drift; never reset or overwrite an incompatible local change.
[CmdletBinding()]
param([Parameter(Mandatory = $true)] [string] $SingBoxSource)

$ErrorActionPreference = 'Stop'
$sourcePath = (Resolve-Path -LiteralPath $SingBoxSource).Path
$patchDirectory = Join-Path $PSScriptRoot 'daemon-patches'

# Windows PowerShell 5.1 promotes a native command's redirected stderr to a
# terminating NativeCommandError while $ErrorActionPreference is 'Stop'. Both
# probes below legitimately write to stderr ("patch does not apply"), and their
# outcome is handled through $LASTEXITCODE, so keep native errors from ending
# the script before those checks run. Thrown conflicts still terminate.
$nativeErrorAction = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    foreach ($patch in (Get-ChildItem -LiteralPath $patchDirectory -Filter '*.patch' | Sort-Object Name)) {
        & git -C $sourcePath apply --reverse --check -- $patch.FullName 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Already applied: $($patch.Name)"
            continue
        }
        & git -C $sourcePath apply --check -- $patch.FullName 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "Cannot apply $($patch.Name): upstream drift or conflicting local edits."
        }
        & git -C $sourcePath apply -- $patch.FullName
        if ($LASTEXITCODE -ne 0) { throw "Failed to apply $($patch.Name)." }
        Write-Host "Applied: $($patch.Name)"
    }
}
finally {
    $ErrorActionPreference = $nativeErrorAction
}

# Do not leak a failed reverse-check exit code to callers.
$global:LASTEXITCODE = 0
