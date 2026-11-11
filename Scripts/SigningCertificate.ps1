# Match the certificate restrictions enforced by the SDK's Add-AppDevPackage.ps1.
function Assert-SfwSigningCertificate {
    param([Parameter(Mandatory = $true)]
        [Security.Cryptography.X509Certificates.X509Certificate2] $Certificate)

    if ($Certificate.NotBefore -gt (Get-Date) -or $Certificate.NotAfter -le (Get-Date)) {
        throw 'Signing certificate is not currently valid.'
    }
    $basic = $Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' }
    if (-not $basic -or $basic.CertificateAuthority) {
        throw 'Signing certificate must contain Basic Constraints with CA=false.'
    }
    $usage = $Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.15' }
    if ($usage -and $usage.KeyUsages -ne [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) {
        throw 'Signing certificate Key Usage must be DigitalSignature only.'
    }
    $eku = $Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
    $usages = @($eku.EnhancedKeyUsages | ForEach-Object { $_.Value })
    if ($usages -notcontains '1.3.6.1.5.5.7.3.3') { throw 'Signing certificate requires the Code Signing EKU.' }
    foreach ($oid in $usages) {
        if ($oid -notin @('1.3.6.1.5.5.7.3.3', '1.3.6.1.4.1.311.10.3.13', '1.3.6.1.4.1.311.84.3.1', '1.3.6.1.4.1.311.84.3.2')) {
            throw "Signing certificate has a forbidden EKU: $oid"
        }
    }
}
