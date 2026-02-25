param(
    [int]$ProcessId = 0,
    [string]$ReportPath = "",
    [switch]$StopAllCloudflared
)

$ErrorActionPreference = "Stop"

function Resolve-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Stop-ByProcessId {
    param([Parameter(Mandatory = $true)][int]$Pid)

    if ($Pid -le 0) {
        return $false
    }

    $proc = Get-Process -Id $Pid -ErrorAction SilentlyContinue
    if ($null -eq $proc) {
        return $false
    }

    Stop-Process -Id $Pid -Force
    return $true
}

$stopped = 0

if ($StopAllCloudflared) {
    $targets = Get-Process cloudflared -ErrorAction SilentlyContinue
    foreach ($target in $targets) {
        Stop-Process -Id $target.Id -Force
        $stopped++
    }

    Write-Host "[Tunnel] Stopped cloudflared processes: $stopped"
    exit 0
}

if ($ProcessId -gt 0) {
    if (Stop-ByProcessId -Pid $ProcessId) {
        Write-Host "[Tunnel] Stopped process id: $ProcessId"
        exit 0
    }

    Write-Host "[Tunnel] Process id not found: $ProcessId"
    exit 1
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportAbs = Resolve-AbsolutePath -Path $ReportPath
    if (-not (Test-Path -LiteralPath $reportAbs)) {
        throw "ReportPath not found: $reportAbs"
    }

    $json = Get-Content -LiteralPath $reportAbs -Raw | ConvertFrom-Json
    if ($null -eq $json.cloudflaredPid) {
        throw "cloudflaredPid not found in report: $reportAbs"
    }

    $pidFromReport = [int]$json.cloudflaredPid
    if (Stop-ByProcessId -Pid $pidFromReport) {
        Write-Host "[Tunnel] Stopped process id from report: $pidFromReport"
        exit 0
    }

    Write-Host "[Tunnel] Process id from report not found: $pidFromReport"
    exit 1
}

throw "No target specified. Use one of: -ProcessId, -ReportPath, -StopAllCloudflared."
