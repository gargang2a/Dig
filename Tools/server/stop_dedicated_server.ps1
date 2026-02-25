param(
    [int]$ProcessId = 0,
    [string]$ReportPath = "",
    [switch]$StopAllByName
)

$ErrorActionPreference = "Stop"

function Resolve-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Stop-ByPid {
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

if ($StopAllByName) {
    $count = 0

    $candidates = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.CommandLine -and (
                $_.CommandLine -match "dw-mode=server" -or
                $_.CommandLine -match "-batchmode"
            )
        }

    foreach ($candidate in $candidates) {
        try {
            Stop-Process -Id $candidate.ProcessId -Force -ErrorAction Stop
            $count++
        } catch {
            # ignore inaccessible or already-exited process
        }
    }

    Write-Host "[Server] Stopped process count by command-line filter: $count"
    exit 0
}

if ($ProcessId -gt 0) {
    if (Stop-ByPid -Pid $ProcessId) {
        Write-Host "[Server] Stopped process id: $ProcessId"
        exit 0
    }

    Write-Host "[Server] Process id not found: $ProcessId"
    exit 1
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportAbs = Resolve-AbsolutePath -Path $ReportPath
    if (-not (Test-Path -LiteralPath $reportAbs -PathType Leaf)) {
        throw "ReportPath not found: $reportAbs"
    }

    $report = Get-Content -LiteralPath $reportAbs -Raw | ConvertFrom-Json
    if ($null -eq $report.serverPid) {
        throw "serverPid not found in report: $reportAbs"
    }

    $pidFromReport = [int]$report.serverPid
    if (Stop-ByPid -Pid $pidFromReport) {
        Write-Host "[Server] Stopped process id from report: $pidFromReport"
        exit 0
    }

    Write-Host "[Server] Process id from report not found: $pidFromReport"
    exit 1
}

throw "No target specified. Use one of: -ProcessId, -ReportPath, -StopAllByName."
