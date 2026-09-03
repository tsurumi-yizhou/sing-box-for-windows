[CmdletBinding()]
param(
    [ValidateSet("x64", "arm64")] [string] $Architecture = "x64",
    [string] $OutputDirectory = "BundleArtifacts",
    [switch] $SkipDaemonBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$daemonPath = Join-Path $repoRoot 'daemon\sing-box-daemon.exe'
$signScript = Join-Path $PSScriptRoot 'Sign-Outputs.ps1'
$buildDaemonScript = Join-Path $PSScriptRoot 'build-daemon.ps1'
$syncScript = Join-Path $PSScriptRoot 'Sync-Upstream.ps1'
$outputPath = Join-Path $repoRoot $OutputDirectory
$certificatePath = Join-Path $outputPath 'SFW_Developer.cer'

if (-not $SkipDaemonBuild) {
    & $syncScript
    if ($LASTEXITCODE -ne 0) { throw 'Upstream synchronization failed.' }
    & $buildDaemonScript -Architecture $Architecture
    if ($LASTEXITCODE -ne 0) { throw 'Daemon build failed.' }
}
if (-not (Test-Path $daemonPath)) {
    throw 'daemon\sing-box-daemon.exe was not found. Build it first with Scripts\build-daemon.ps1.'
}

& $signScript -Paths $daemonPath -ExportCertificatePath $certificatePath
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq 'CN=sing-box-for-windows-dev' -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $cert) { throw 'The local development signing certificate could not be found.' }

dotnet publish (Join-Path $repoRoot 'SFW.csproj') -c Release -r "win-$Architecture" `
    --self-contained true -p:Platform=$Architecture -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageDir="$outputPath\" -p:PackageCertificateThumbprint=$($cert.Thumbprint)