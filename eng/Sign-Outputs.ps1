# Signs the app and daemon with a shared self-signed code-signing certificate.
# boxdd's peer authentication requires the application and daemon to carry the
# same Authenticode signer; self-signed certificates are accepted by design
# (see validateUntrustedSelfSignedCertificate in boxdd). The certificate lives
# in the current user's store and is created once.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string[]] $Paths,
    [string] $CertificateSubject = "CN=sing-box-for-windows-dev"
)

$ErrorActionPreference = 'Stop'

# MSBuild's Exec task goes through cmd.exe. When that build originates in a
# Microsoft Store PowerShell 7 session, Windows PowerShell 5.1 inherits a
# PSModulePath whose first Security module is PS7's incompatible .NET build.
# Do not let module auto-discovery choose it: load the inbox 5.1 manifest by
# its absolute path. This restores the Cert: provider and Authenticode cmdlets.
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    $windowsPowerShellModules = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules'
    $securityModuleManifest = Join-Path $windowsPowerShellModules 'Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1'
    if (-not (Test-Path -LiteralPath $securityModuleManifest)) {
        throw "Windows PowerShell Security module was not found: $securityModuleManifest"
    }
    $env:PSModulePath = "$env:ProgramFiles\WindowsPowerShell\Modules;$windowsPowerShellModules"
    Import-Module -Name $securityModuleManifest -Force
}

# powershell -File (used by the MSBuild Exec task) does not parse
# comma-separated arguments as arrays, so accept ';'-joined lists too.
$Paths = $Paths | ForEach-Object { $_ -split ';' } | Where-Object { $_ }

# Avoid the -CodeSigningCert dynamic parameter: its availability depends on
# the Certificate provider being usable in the caller's environment (it is
# not, for example, under some MSBuild/sandboxed Exec hosts). Filter by EKU.
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object {
        $_.Subject -eq $CertificateSubject -and $_.HasPrivateKey -and
        ($_.EnhancedKeyUsageList | Where-Object ObjectId -eq '1.3.6.1.5.5.7.3.3')
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    Write-Host "Creating self-signed code-signing certificate '$CertificateSubject'..."
    $cert = New-SelfSignedCertificate `
        -Subject $CertificateSubject `
        -Type CodeSigningCert `
        -KeyAlgorithm RSA -KeyLength 2048 `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter ([datetime]::Now.AddYears(10)) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
}

foreach ($path in $Paths) {
    if (-not (Test-Path $path)) { throw "Not found: $path" }

    # Authenticode signing rewrites the executable even when it already has the
    # expected signature. Avoid changing its timestamp on every ordinary app
    # build: the running Windows service holds the copied daemon executable
    # open, and MSBuild can then safely skip an unchanged PreserveNewest copy.
    $signature = Get-AuthenticodeSignature -FilePath $path
    if ($signature.SignerCertificate -and
        $signature.SignerCertificate.Thumbprint -eq $cert.Thumbprint) {
        Write-Host "Already signed: $path"
        continue
    }

    $null = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256
    # Set-AuthenticodeSignature reports UnknownError for self-signed chains
    # ("terminated in a root certificate which is not trusted") even though the
    # signature was written; boxdd accepts self-signed signers by design.
    # Verify the applied signature instead of trusting the returned status.
    $signature = Get-AuthenticodeSignature -FilePath $path
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
        throw "Signing failed for ${path}: $($signature.Status) $($signature.StatusMessage)"
    }
    Write-Host "Signed: $path"
}
