param(
    [int]$LocalPort = 7778,
    [string]$SiteUrl = "https://gargang2a.github.io/Dig/",
    [string]$OutputDir = ".\build\github-pages",
    [int]$WaitSeconds = 30,
    [switch]$InstallIfMissing,
    [switch]$KillExisting
)

$ErrorActionPreference = "Stop"

function Resolve-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Resolve-CloudflaredPath {
    $command = Get-Command cloudflared -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $registryKeys = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )

    $registryPath = Get-ItemProperty -Path $registryKeys -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "*cloudflared*" } |
        Select-Object -First 1 -ExpandProperty InstallLocation

    if (-not [string]::IsNullOrWhiteSpace($registryPath)) {
        $registryExe = Join-Path $registryPath "cloudflared.exe"
        if (Test-Path -LiteralPath $registryExe) {
            return $registryExe
        }
    }

    $candidates = @(
        "$env:ProgramFiles\cloudflared\cloudflared.exe",
        "$env:ProgramFiles(x86)\cloudflared\cloudflared.exe",
        "$env:LOCALAPPDATA\Programs\cloudflared\cloudflared.exe",
        "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Cloudflare.cloudflared_Microsoft.Winget.Source_8wekyb3d8bbwe\cloudflared.exe",
        "$env:LOCALAPPDATA\Microsoft\WinGet\Links\cloudflared.exe",
        "$env:ChocolateyInstall\bin\cloudflared.exe"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    return $null
}

function Install-Cloudflared {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "winget is required to auto-install cloudflared."
    }

    Write-Host "[Tunnel] cloudflared is missing. Installing with winget..."
    winget install --id Cloudflare.cloudflared -e --accept-package-agreements --accept-source-agreements --disable-interactivity
}

function Wait-TunnelUrlFromLog {
    param(
        [Parameter(Mandatory = $true)][string[]]$LogPaths,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $regex = [regex]'https://[a-z0-9-]+\.trycloudflare\.com'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        foreach ($logPath in $LogPaths) {
            if (Test-Path -LiteralPath $logPath) {
                $content = Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue
                if (-not [string]::IsNullOrWhiteSpace($content)) {
                    $match = $regex.Match($content)
                    if ($match.Success) {
                        return $match.Value
                    }
                }
            }
        }

        Start-Sleep -Milliseconds 400
    }

    return $null
}

function New-MarkdownReport {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][hashtable]$Result
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Friend Web Test Tunnel Report")
    $lines.Add("")
    $lines.Add("- CreatedAtUtc: $($Result.createdAtUtc)")
    $lines.Add("- Status: $($Result.status)")
    $lines.Add("- LocalPort: $($Result.localPort)")
    $lines.Add("- LocalPortOpen: $($Result.localPortOpen)")
    $lines.Add("- CloudflaredPath: $($Result.cloudflaredPath)")
    $lines.Add("- CloudflaredPid: $($Result.cloudflaredPid)")
    $lines.Add("- TunnelUrl: $($Result.tunnelUrl)")
    $lines.Add("- FriendJoinUrl: $($Result.friendJoinUrl)")
    $lines.Add("- LogPath: $($Result.logPath)")
    $lines.Add("- ErrorLogPath: $($Result.errorLogPath)")
    $lines.Add("")

    if ($Result.notes.Count -gt 0) {
        $lines.Add("## Notes")
        $lines.Add("")
        foreach ($note in $Result.notes) {
            $lines.Add("- $note")
        }
    }

    $lines -join "`n" | Set-Content -LiteralPath $Path -Encoding UTF8
}

if ($LocalPort -le 0 -or $LocalPort -gt 65535) {
    throw "LocalPort must be between 1 and 65535."
}

$outputDirAbs = Resolve-AbsolutePath -Path $OutputDir
if (-not (Test-Path -LiteralPath $outputDirAbs)) {
    New-Item -ItemType Directory -Path $outputDirAbs | Out-Null
}

$cloudflaredPath = Resolve-CloudflaredPath
if ($null -eq $cloudflaredPath -and $InstallIfMissing) {
    Install-Cloudflared
    $cloudflaredPath = Resolve-CloudflaredPath
}

if ($null -eq $cloudflaredPath) {
    throw "cloudflared not found. Run 'winget install Cloudflare.cloudflared -e' and retry."
}

if ($KillExisting) {
    Get-Process cloudflared -ErrorAction SilentlyContinue | Stop-Process -Force
}

$portOpen = $false
try {
    $portProbe = Test-NetConnection -ComputerName "127.0.0.1" -Port $LocalPort -WarningAction SilentlyContinue
    $portOpen = [bool]$portProbe.TcpTestSucceeded
} catch {
    $portOpen = $false
}

$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd_HHmmss")
$logPath = Join-Path $outputDirAbs ("friend_tunnel_" + $timestamp + ".log")
$logErrPath = Join-Path $outputDirAbs ("friend_tunnel_" + $timestamp + ".err.log")
$reportMd = Join-Path $outputDirAbs ("friend_tunnel_" + $timestamp + ".md")
$reportJson = [System.IO.Path]::ChangeExtension($reportMd, ".json")

$arguments = @(
    "tunnel"
    "--url"
    "http://127.0.0.1:$LocalPort"
    "--no-autoupdate"
)

$proc = Start-Process -FilePath $cloudflaredPath `
    -ArgumentList $arguments `
    -RedirectStandardOutput $logPath `
    -RedirectStandardError $logErrPath `
    -WindowStyle Hidden `
    -PassThru

$tunnelUrl = Wait-TunnelUrlFromLog -LogPaths @($logPath, $logErrPath) -TimeoutSeconds $WaitSeconds

if ([string]::IsNullOrWhiteSpace($tunnelUrl)) {
    $tailParts = New-Object System.Collections.Generic.List[string]
    if (Test-Path -LiteralPath $logPath) {
        $tailParts.Add("STDOUT:")
        $tailParts.Add((Get-Content -LiteralPath $logPath -Tail 30) -join "`n")
    }
    if (Test-Path -LiteralPath $logErrPath) {
        $tailParts.Add("STDERR:")
        $tailParts.Add((Get-Content -LiteralPath $logErrPath -Tail 30) -join "`n")
    }
    $tail = $tailParts -join "`n"

    throw "Failed to acquire tunnel URL within WaitSeconds=$WaitSeconds. Check logs: $logPath, $logErrPath`n$tail"
}

$tunnelHost = ([System.Uri]$tunnelUrl).Host
$separator = if ($SiteUrl.Contains("?")) { "&" } else { "?" }
$friendUrl = "$SiteUrl$separator" + "server=$tunnelHost&port=443&wss=1"

$notes = New-Object System.Collections.Generic.List[string]
if (-not $portOpen) {
    $notes.Add("No response detected on local port $LocalPort. Start Host/Server first.")
}

$result = [ordered]@{
    createdAtUtc    = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    status          = "PASS"
    localPort       = $LocalPort
    localPortOpen   = $portOpen
    cloudflaredPath = $cloudflaredPath
    cloudflaredPid  = $proc.Id
    tunnelUrl       = $tunnelUrl
    friendJoinUrl   = $friendUrl
    logPath         = $logPath
    errorLogPath    = $logErrPath
    notes           = $notes
}

New-MarkdownReport -Path $reportMd -Result $result
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportJson -Encoding UTF8

Write-Host "[Tunnel] Status: PASS"
Write-Host "[Tunnel] Cloudflared PID: $($proc.Id)"
Write-Host "[Tunnel] Tunnel URL: $tunnelUrl"
Write-Host "[Tunnel] Friend Join URL: $friendUrl"
Write-Host "[Tunnel] Report(MD): $reportMd"
Write-Host "[Tunnel] Report(JSON): $reportJson"
