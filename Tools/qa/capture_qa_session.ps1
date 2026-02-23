param(
    [Parameter(Mandatory = $true)]
    [string]$HostLogPath,

    [string]$ClientLogPath,

    [string]$OutputRoot = "Docs/qa_sessions",

    [string]$SessionId,

    [string]$StartAfterPattern,

    [int]$TailLineCount = 0,

    [switch]$SkipKpiSummary,

    [switch]$SkipQa001Summary
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-ExistingFilePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PathValue,

        [Parameter(Mandatory = $true)]
        [string]$ParamName
    )

    $expanded = [Environment]::ExpandEnvironmentVariables($PathValue)
    if (-not (Test-Path -Path $expanded -PathType Leaf)) {
        throw "$ParamName not found: $PathValue"
    }

    return (Resolve-Path -Path $expanded).Path
}

function Export-LogSlices {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$Prefix,
        [Parameter(Mandatory = $true)][string]$SessionDir,
        [string]$StartAfterPattern,
        [int]$TailLineCount = 0
    )

    $lines = Get-Content -Path $SourcePath -Encoding UTF8

    if (-not [string]::IsNullOrWhiteSpace($StartAfterPattern) -and @($lines).Count -gt 0) {
        $startIndex = -1
        for ($idx = @($lines).Count - 1; $idx -ge 0; $idx--) {
            if ($lines[$idx] -match $StartAfterPattern) {
                $startIndex = $idx
                break
            }
        }

        if ($startIndex -ge 0) {
            if ($startIndex -lt (@($lines).Count - 1)) {
                $lines = $lines[($startIndex + 1)..(@($lines).Count - 1)]
            }
            else {
                $lines = @()
            }
        }
    }

    if ($TailLineCount -gt 0 -and @($lines).Count -gt $TailLineCount) {
        $lines = $lines[(@($lines).Count - $TailLineCount)..(@($lines).Count - 1)]
    }

    $kpiPattern = "\[QA\]\[KPI\]\["
    $networkPattern = "\[Network\]|\[PvP\]|\[Assault Kill\]|StartGame|ServerRejectMessage|OnServerDisconnect|OnClientDisconnect|KillPlayer|GameOver|SWT-ClientSend|SimpleWebTransport|AutoStart Policy|MaxConnections|sendRate|CmdRespawn skipped|UI References are missing"
    $errorPattern = "\b(Exception|Error|ERROR|NullReferenceException|MissingReferenceException|Assertion failed|Crash)\b|Not Connected"
    $warningPattern = "\bWarning\b|\bWARN\b|\bCS\d{4}\b"

    $kpiLines = $lines | Where-Object { $_ -match $kpiPattern }
    $networkLines = $lines | Where-Object { $_ -match $networkPattern }
    $errorLines = $lines | Where-Object { $_ -match $errorPattern }
    $warningLines = $lines | Where-Object { $_ -match $warningPattern }

    $kpiPath = Join-Path -Path $SessionDir -ChildPath "$Prefix`_qa_kpi.log"
    $networkPath = Join-Path -Path $SessionDir -ChildPath "$Prefix`_network.log"
    $errorPath = Join-Path -Path $SessionDir -ChildPath "$Prefix`_errors.log"
    $warningPath = Join-Path -Path $SessionDir -ChildPath "$Prefix`_warnings.log"

    Set-Content -Path $kpiPath -Encoding UTF8 -Value $kpiLines
    Set-Content -Path $networkPath -Encoding UTF8 -Value $networkLines
    Set-Content -Path $errorPath -Encoding UTF8 -Value $errorLines
    Set-Content -Path $warningPath -Encoding UTF8 -Value $warningLines

    return [PSCustomObject]@{
        Prefix        = $Prefix
        SourcePath    = $SourcePath
        KpiCount      = @($kpiLines).Count
        NetworkCount  = @($networkLines).Count
        ErrorCount    = @($errorLines).Count
        WarningCount  = @($warningLines).Count
        KpiPath       = $kpiPath
        NetworkPath   = $networkPath
        ErrorPath     = $errorPath
        WarningPath   = $warningPath
        KpiSummary    = ""
    }
}

if ([string]::IsNullOrWhiteSpace($SessionId)) {
    $SessionId = Get-Date -Format "yyyy-MM-dd_HHmmss"
}

$resolvedHostPath = Resolve-ExistingFilePath -PathValue $HostLogPath -ParamName "HostLogPath"
$resolvedClientPath = $null
if (-not [string]::IsNullOrWhiteSpace($ClientLogPath)) {
    $resolvedClientPath = Resolve-ExistingFilePath -PathValue $ClientLogPath -ParamName "ClientLogPath"
}

$sessionDir = Join-Path -Path $OutputRoot -ChildPath $SessionId
New-Item -Path $sessionDir -ItemType Directory -Force | Out-Null

$hostSlice = Export-LogSlices `
    -SourcePath $resolvedHostPath `
    -Prefix "host" `
    -SessionDir $sessionDir `
    -StartAfterPattern $StartAfterPattern `
    -TailLineCount $TailLineCount
$clientSlice = $null
if ($resolvedClientPath) {
    $clientSlice = Export-LogSlices `
        -SourcePath $resolvedClientPath `
        -Prefix "client" `
        -SessionDir $sessionDir `
        -StartAfterPattern $StartAfterPattern `
        -TailLineCount $TailLineCount
}

if (-not $SkipKpiSummary) {
    $parserPath = Join-Path -Path $PSScriptRoot -ChildPath "parse_qa_kpi_log.ps1"
    if (Test-Path -Path $parserPath -PathType Leaf) {
        if ($hostSlice.KpiCount -gt 0) {
            $hostSummaryPath = Join-Path -Path $sessionDir -ChildPath "qa_kpi_summary_host.md"
            & $parserPath -InputPath $hostSlice.KpiPath -OutputPath $hostSummaryPath | Out-Null
            $hostSlice.KpiSummary = $hostSummaryPath
        }

        if ($clientSlice -and $clientSlice.KpiCount -gt 0) {
            $clientSummaryPath = Join-Path -Path $sessionDir -ChildPath "qa_kpi_summary_client.md"
            & $parserPath -InputPath $clientSlice.KpiPath -OutputPath $clientSummaryPath | Out-Null
            $clientSlice.KpiSummary = $clientSummaryPath
        }
    }
}

$qa001SummaryPath = ""
if (-not $SkipQa001Summary) {
    $qa001SummaryScriptPath = Join-Path -Path $PSScriptRoot -ChildPath "summarize_qa001_capture.ps1"
    if (Test-Path -Path $qa001SummaryScriptPath -PathType Leaf) {
        try {
            $qa001SummaryPath = Join-Path -Path $sessionDir -ChildPath "qa001_log_check_summary.md"
            & $qa001SummaryScriptPath -SessionDir $sessionDir -OutputPath $qa001SummaryPath | Out-Null
        }
        catch {
            Write-Warning ("QA-001 summary generation failed: {0}" -f $_.Exception.Message)
            $qa001SummaryPath = ""
        }
    }
}

$generatedAt = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$sourceRows = @(
    "| host | $($hostSlice.SourcePath) | $($hostSlice.KpiCount) | $($hostSlice.NetworkCount) | $($hostSlice.ErrorCount) | $($hostSlice.WarningCount) |"
)
if ($clientSlice) {
    $sourceRows += "| client | $($clientSlice.SourcePath) | $($clientSlice.KpiCount) | $($clientSlice.NetworkCount) | $($clientSlice.ErrorCount) | $($clientSlice.WarningCount) |"
}

$artifactRows = @(
    "| host QA KPI | $($hostSlice.KpiPath) |",
    "| host network | $($hostSlice.NetworkPath) |",
    "| host errors | $($hostSlice.ErrorPath) |",
    "| host warnings | $($hostSlice.WarningPath) |"
)

if ($hostSlice.KpiSummary) {
    $artifactRows += "| host KPI summary | $($hostSlice.KpiSummary) |"
}

if ($clientSlice) {
    $artifactRows += "| client QA KPI | $($clientSlice.KpiPath) |"
    $artifactRows += "| client network | $($clientSlice.NetworkPath) |"
    $artifactRows += "| client errors | $($clientSlice.ErrorPath) |"
    $artifactRows += "| client warnings | $($clientSlice.WarningPath) |"

    if ($clientSlice.KpiSummary) {
        $artifactRows += "| client KPI summary | $($clientSlice.KpiSummary) |"
    }
}

if (-not [string]::IsNullOrWhiteSpace($qa001SummaryPath)) {
    $artifactRows += "| QA-001 log check summary | $qa001SummaryPath |"
}

$report = @"
# QA Session Capture Report

- GeneratedAt: $generatedAt
- SessionId: $SessionId
- SessionDir: $sessionDir
- StartAfterPattern: $StartAfterPattern
- TailLineCount: $TailLineCount

## Source Stats

| Source | Path | KPI | Network | Errors | Warnings |
| :--- | :--- | ---: | ---: | ---: | ---: |
$($sourceRows -join [Environment]::NewLine)

## Artifacts

| Artifact | Path |
| :--- | :--- |
$($artifactRows -join [Environment]::NewLine)

## Next Steps

1. Copy FAIL/CHECK evidence from `*_errors.log`, `*_network.log` to `Docs/playtest_checklist_net_2026-02-22.md`.
2. If `qa_kpi_summary_*.md` exists, attach summary values to QA-001 result section.
3. If `qa001_log_check_summary.md` exists, copy auto-pass/hold result and manual-check list to checklist/task docs.
4. Sync final pass/fail and follow-up items to Notion worklog page.
"@

$reportPath = Join-Path -Path $sessionDir -ChildPath "session_report.md"
Set-Content -Path $reportPath -Encoding UTF8 -Value $report

Write-Output "QA session artifacts written to: $sessionDir"
Write-Output "QA session report: $reportPath"
