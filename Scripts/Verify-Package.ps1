[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PackagePath,
    [Parameter(Mandatory = $true)] [string] $ExpectedCertificatePath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($ExpectedCertificatePath)
. (Join-Path $PSScriptRoot 'SigningCertificate.ps1')
Assert-SfwSigningCertificate -Certificate $certificate
$packageSignature = Get-AuthenticodeSignature -LiteralPath $PackagePath
if ($packageSignature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
    $packageSignature.Status -notin @('Valid', 'UnknownError', 'NotTrusted')) {
    throw "Invalid or inconsistent MSIX signature: $($packageSignature.Status)"
}
# UnknownError/NotTrusted is expected on a build machine that has not trusted
# the self-signed certificate. HashMismatch and NotSigned must always fail.
Write-Host "Verified MSIX signer: $($packageSignature.Status)"
$packageDirectory = Split-Path -Parent $PackagePath
$verificationDirectory = Join-Path $packageDirectory ('.verify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $verificationDirectory | Out-Null
$archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
try {
    $entries = @{}
    foreach ($entry in $archive.Entries) { $entries[$entry.FullName.Replace('\', '/')] = $entry }
    foreach ($required in @('AppxManifest.xml', 'AppxSignature.p7x', 'coreclr.dll', 'hostfxr.dll',
        'sing-box.exe', 'resources/daemon/sing-box-daemon.exe', 'resources/daemon/WinDivert64.sys')) {
        if (-not $entries.ContainsKey($required)) { throw "Package is missing $required" }
    }
    $reader = [IO.StreamReader]::new($entries['AppxManifest.xml'].Open())
    try { $manifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($manifest.Package.Identity.Publisher -ne $certificate.Subject) { throw 'Package publisher does not match certificate.' }
    if ($manifest.Package.Identity.ProcessorArchitecture -ne 'x64') { throw 'Expected an x64 package.' }
    foreach ($dependency in $manifest.Package.Dependencies.PackageDependency) {
        $dependencyPath = Join-Path $packageDirectory "Dependencies\x64\$($dependency.Name).msix"
        if (-not (Test-Path -LiteralPath $dependencyPath)) { throw "Missing offline dependency: $dependencyPath" }
    }
    foreach ($executable in @('sing-box.exe', 'resources/daemon/sing-box-daemon.exe')) {
        $destination = Join-Path $verificationDirectory ([IO.Path]::GetFileName($executable))
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[$executable], $destination)
        $signature = Get-AuthenticodeSignature -LiteralPath $destination
        if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
            $signature.Status -notin @('Valid', 'UnknownError', 'NotTrusted')) {
            throw "Invalid or inconsistent executable signature: $executable ($($signature.Status))"
        }
        Write-Host "Verified signer: $executable ($($signature.Status))"
    }
    $publicCertificates = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.cer')
    if (-not ($publicCertificates | Where-Object {
        ([Security.Cryptography.X509Certificates.X509Certificate2]::new($_.FullName)).Thumbprint -eq $certificate.Thumbprint
    })) { throw 'Distribution is missing the matching public certificate.' }
    # -Include is ignored for -LiteralPath in Windows PowerShell 5.1, so filter
    # the recursive file list by hand instead of letting every item match.
    $privateKeyFiles = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File |
        Where-Object { $_.Extension -in '.pfx', '.p12', '.key' })
    if ($privateKeyFiles.Count -gt 0) {
        throw "Private-key material must not be distributed: $($privateKeyFiles[0].Name)"
    }
    Write-Host "Verified MSIX $($manifest.Package.Identity.Version): runtime, daemon, signatures and offline dependencies."
}
finally {
    $archive.Dispose()
    # Only remove the uniquely created verification directory inside this package directory.
    $resolvedVerification = [IO.Path]::GetFullPath($verificationDirectory)
    $resolvedParent = [IO.Path]::GetFullPath($packageDirectory).TrimEnd('\') + '\'
    if ($resolvedVerification.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedVerification -Recurse -Force
    }
}
