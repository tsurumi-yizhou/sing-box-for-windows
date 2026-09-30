# Build first, then stop the service, replace its locked resources, and restart.
# Keep this separate from Build: ordinary builds must not interrupt a live VPN.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [switch] $ElevatedCopy,
    [switch] $SmokeTest,
    [string] $ResultPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$layout = Join-Path $repoRoot '.daemon-install'
$source = Join-Path $repoRoot 'daemon'
$destination = Join-Path $layout 'resources\daemon'
$cache = Join-Path $repoRoot '.cache'

if ($ElevatedCopy) {
    # Windows PowerShell must not import PowerShell 7's incompatible modules.
    $env:PSModulePath = "$env:ProgramFiles\WindowsPowerShell\Modules;$env:SystemRoot\System32\WindowsPowerShell\v1.0\Modules"
    if (-not $ResultPath -or -not [IO.Path]::GetFullPath($ResultPath).StartsWith(
        $cache + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid result path.' }
    try {
        $service = Get-Service sing-box-daemon -ErrorAction SilentlyContinue
        if ($service) {
            $registered = Get-CimInstance Win32_Service -Filter "Name='sing-box-daemon'"
            $expected = Join-Path $destination 'sing-box-daemon.exe'
            if ($registered.PathName -notmatch ('^"?' + [regex]::Escape($expected) + '"?(\s|$)')) {
                throw 'The installed daemon belongs to another layout. Repair its registration before updating this layout.'
            }
        }
        $wasRunning = $service -and $service.Status -ne 'Stopped'
        $backup = Join-Path $cache ('daemon-backup-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $backup, $destination | Out-Null
        if ($service) {
            Stop-Service sing-box-daemon
            $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(10))
        }
        try {
            foreach ($file in (Get-ChildItem -LiteralPath $source -File)) {
                $target = Join-Path $destination $file.Name
                if (Test-Path -LiteralPath $target) { Copy-Item -LiteralPath $target -Destination $backup }
                Copy-Item -LiteralPath $file.FullName -Destination $target -Force
                if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                    throw "Resource verification failed: $($file.Name)"
                }
            }
        } catch {
            foreach ($file in (Get-ChildItem -LiteralPath $backup -File)) {
                Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
            }
            throw
        } finally {
            if ($wasRunning) { Start-Service sing-box-daemon }
        }
        [IO.File]::WriteAllText($ResultPath, 'SUCCESS')
        exit 0
    } catch {
        [IO.File]::WriteAllText($ResultPath, $_.ToString())
        exit 1
    }
}

Push-Location $repoRoot
try {
    $published = Join-Path $cache 'release-publish'
    if ($Configuration -eq 'Release') {
        dotnet publish SFW.csproj -c Release -p:Platform=x64 -r win-x64 --output $published
    } else {
        dotnet build SFW.csproj -c Debug -p:Platform=x64 -r win-x64
    }
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; the running layout was not touched.' }
    $appPath = Join-Path $layout 'sing-box.exe'
    Get-Process sing-box -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $appPath } | Stop-Process
    New-Item -ItemType Directory -Force $cache | Out-Null
    $changed = @(Get-ChildItem -LiteralPath $source -File | Where-Object {
        $target = Join-Path $destination $_.Name
        -not (Test-Path -LiteralPath $target) -or
            (Get-FileHash -LiteralPath $_.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash
    })
    if ($changed.Count -gt 0) {
        $result = Join-Path $cache ('daemon-update-' + [Guid]::NewGuid().ToString('N') + '.txt')
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -ElevatedCopy -ResultPath "{1}"' -f $PSCommandPath, $result
        Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait
        if (-not (Test-Path -LiteralPath $result)) { throw 'The elevated update did not report a result.' }
        $outcome = [IO.File]::ReadAllText($result)
        if ($outcome -ne 'SUCCESS') { throw $outcome }
    }
    if ($Configuration -eq 'Release') {
        # dotnet run stages Build output, which does not contain R2R native code.
        $properties = dotnet msbuild SFW.csproj -p:Configuration=Release -p:Platform=x64 `
            -getProperty:WinAppCliPath,WinAppManifestPath | ConvertFrom-Json
        $launchArgs = if ($SmokeTest) { @('--', '--ui-smoke-test') } else { @() }
        & $properties.Properties.WinAppCliPath run $published --manifest $properties.Properties.WinAppManifestPath `
            --output-appx-directory $layout --executable sing-box.exe --detach @launchArgs
    } else {
        $launchArgs = if ($SmokeTest) { @('--', '--', '--ui-smoke-test') } else { @() }
        dotnet run --project SFW.csproj -c Debug --no-build -p:Platform=x64 -r win-x64 @launchArgs
    }
    if ($LASTEXITCODE -ne 0) { throw 'The application launch failed.' }
} finally {
    Pop-Location
}
