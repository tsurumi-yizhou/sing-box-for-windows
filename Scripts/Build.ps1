[CmdletBinding()]
param(
    [ValidateSet("x64")] [string] $Architecture = "x64",
    [string] $OutputDirectory = "BundleArtifacts",
    [switch] $SkipDaemonBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$daemonPath = Join-Path $repoRoot 'daemon\sing-box-daemon.exe'
$signScript = Join-Path $PSScriptRoot 'Sign-Outputs.ps1'
$buildDaemonScript = Join-Path $PSScriptRoot 'build-daemon.ps1'
$syncScript = Join-Path $PSScriptRoot 'Sync-Upstream.ps1'
$manifest = [xml](Get-Content (Join-Path $repoRoot 'Package.appxmanifest') -Raw)
$version = $manifest.Package.Identity.Version
# A fresh directory prevents a failed build from distributing an older package.
$releaseName = "SFW_${version}_${Architecture}_$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$outputPath = Join-Path (Join-Path $repoRoot $OutputDirectory) $releaseName
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
# Use exactly the certificate selected by Sign-Outputs, even if an older,
# incompatible certificate with the same subject remains in the local store.
$exportedCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
$cert = Get-Item "Cert:\CurrentUser\My\$($exportedCertificate.Thumbprint)"
if (-not $cert) { throw 'The local development signing certificate could not be found.' }

dotnet publish (Join-Path $repoRoot 'SFW.csproj') -c Release -r "win-$Architecture" `
    --self-contained true -p:Platform=$Architecture -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageDir="$outputPath\" -p:PackageCertificateThumbprint=$($cert.Thumbprint) `
    -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxBundle=Never `
    -p:AppxPackageSigningEnabled=true -p:AppxLogTelemetryFromSideloadingScript=false
if ($LASTEXITCODE -ne 0) { throw "Publishing failed (exit code $LASTEXITCODE). No distribution ZIP was created." }

$packages = @(Get-ChildItem -LiteralPath $outputPath -Recurse -Filter '*.msix' |
    Where-Object { $_.Directory.Name -ne 'x64' -and $_.FullName -notmatch '[\\/]Dependencies[\\/]' })
if ($packages.Count -ne 1) { throw "Expected one application MSIX; found $($packages.Count)." }
$package = $packages[0]
$distributionPath = $package.Directory.FullName
if (-not (Test-Path (Join-Path $distributionPath 'Add-AppDevPackage.ps1'))) {
    throw 'The SDK did not generate its installation script.'
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-SFW.cmd') -Destination $distributionPath
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'INSTALL.txt') -Destination $distributionPath
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $distributionPath

& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PackagePath $package.FullName -ExpectedCertificatePath $certificatePath
& (Join-Path $repoRoot 'Tests\Test-InstallerCertificate.ps1') `
    -InstallerScript (Join-Path $distributionPath 'Add-AppDevPackage.ps1') -CertificatePath $certificatePath
$zipPath = Join-Path $outputPath "$releaseName.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($distributionPath, $zipPath)
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ascii
Write-Host "Distribution ZIP: $zipPath"
Write-Host "SHA256: $hash"
