[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $InstallerScript,
    [Parameter(Mandatory = $true)] [string] $CertificatePath,
    [string] $RejectedCertificatePath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Scripts\SigningCertificate.ps1')

# Execute the SDK's actual restriction check without executing its installer,
# importing certificates, acquiring licenses, or modifying the machine.
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $InstallerScript).Path, [ref]$null, [ref]$parseErrors)
if ($parseErrors) { throw 'Unable to parse the SDK installer.' }
$restrictionFunction = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'CheckCertificateRestrictions'
}, $true)
if (-not $restrictionFunction) { throw 'SDK certificate restriction check was not found.' }
. ([scriptblock]::Create($restrictionFunction.Extent.Text))
$ErrorCodes = @{ CertIsCA = 18; BannedEKU = 19; NoBasicConstraints = 20;
    NoCodeSigningEku = 21; BannedKeyUsage = 23 }
$UiStrings = @{}
function PrintMessageAndExit($Message, $Code) { throw "SDK certificate rejection: $Code" }

$CertificatePath = (Resolve-Path $CertificatePath).Path
Assert-SfwSigningCertificate -Certificate ([Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath))
CheckCertificateRestrictions
Write-Host 'PASS: exported certificate passes the actual SDK installer restrictions.'

if ($RejectedCertificatePath) {
    $CertificatePath = (Resolve-Path $RejectedCertificatePath).Path
    $rejected = $false
    try { CheckCertificateRestrictions } catch {
        if ($_.Exception.Message -ne 'SDK certificate rejection: 20') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Regression: the old certificate was expected to fail with missing Basic Constraints.' }
    $rejected = $false
    try {
        Assert-SfwSigningCertificate -Certificate ([Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath))
    } catch { $rejected = $true }
    if (-not $rejected) { throw 'Regression: the build validator accepted the old certificate.' }
    Write-Host 'PASS: old certificate reproduces SDK error 20 and is rejected by the build validator.'
}
