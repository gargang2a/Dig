param(
    [Parameter(Mandatory = $true)]
    [string]$SessionDir,

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -Path $SessionDir -PathType Container)) {
    throw "SessionDir not found: $SessionDir"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path -Path $SessionDir -ChildPath "qa001_log_check_summary.md"
}

function Read-Lines {
    param([string]$Path)

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        return @()
    }

    $raw = Get-Content -Path $Path -Encoding UTF8
    if ($null -eq $raw) { return @() }
    if ($raw -is [string]) { return @($raw) }
    return @($raw)
}

function Count-Match {
    param(
        [string[]]$Lines,
        [string]$Pattern
    )

    if ($null -eq $Lines -or @($Lines).Count -eq 0) {
        return 0
    }

    return @($Lines | Where-Object { $_ -match $Pattern }).Count
}

function First-Matches {
    param(
        [string[]]$Lines,
        [string]$Pattern,
        [int]$Limit = 2
    )

    if ($null -eq $Lines -or @($Lines).Count -eq 0) {
        return @()
    }

    return @($Lines | Where-Object { $_ -match $Pattern } | Select-Object -First $Limit)
}

function Status-FromCondition {
    param(
        [bool]$Condition,
        [string]$Pass = "AUTO_PASS",
        [string]$Fail = "AUTO_FAIL"
    )

    if ($Condition) {
        return $Pass
    }

    return $Fail
}

function Count-KpiMetricAtLeast {
    param(
        [string[]]$Lines,
        [string]$Mode,
        [ValidateSet("conn", "players")]
        [string]$Metric,
        [int]$Threshold
    )

    if ($null -eq $Lines -or @($Lines).Count -eq 0) {
        return 0
    }

    $count = 0
    foreach ($line in $Lines) {
        if ($line -notmatch ("\\[QA\\]\\[KPI\\].*mode={0}" -f $Mode)) {
            continue
        }

        if ($Metric -eq "players") {
            if ($line -match "players=(\d+)/") {
                if ([int]$Matches[1] -ge $Threshold) {
                    $count++
                }
            }
            continue
        }

        if ($line -match "conn=(\d+)") {
            if ([int]$Matches[1] -ge $Threshold) {
                $count++
            }
        }
    }

    return $count
}

function Count-PlayerVsPlayerEvidence {
    param([string[]]$Lines)

    if ($null -eq $Lines -or @($Lines).Count -eq 0) {
        return 0
    }

    $count = 0
    foreach ($line in $Lines) {
        if ($line -match "\[PvP\]\s*(.+?)\s*->\s*(.+?)\s*(처치|Kill)") {
            $attacker = $Matches[1].Trim()
            $target = $Matches[2].Trim()

            if ($attacker -match "^Bot_") { continue }
            if ($target -match "^Bot_") { continue }
            if ($attacker -eq $target) { continue }

            $count++
        }
    }

    return $count
}

$hostNetwork = @(Read-Lines (Join-Path -Path $SessionDir -ChildPath "host_network.log"))
$hostErrors = @(Read-Lines (Join-Path -Path $SessionDir -ChildPath "host_errors.log"))
$hostWarnings = @(Read-Lines (Join-Path -Path $SessionDir -ChildPath "host_warnings.log"))
$hostKpi = @(Read-Lines (Join-Path -Path $SessionDir -ChildPath "host_qa_kpi.log"))
$clientNetwork = @(Read-Lines (Join-Path -Path $SessionDir -ChildPath "client_network.log"))
$clientErrors = @(Read-Lines (Join-Path -Path $SessionDir -ChildPath "client_errors.log"))
$clientWarnings = @(Read-Lines (Join-Path -Path $SessionDir -ChildPath "client_warnings.log"))
$clientKpi = @(Read-Lines (Join-Path -Path $SessionDir -ChildPath "client_qa_kpi.log"))

$networkLines = @($hostNetwork + $clientNetwork)
$errorLines = @($hostErrors + $clientErrors)
$warningLines = @($hostWarnings + $clientWarnings)
$kpiLines = @($hostKpi + $clientKpi)
$allCheckLines = @($networkLines + $errorLines + $warningLines + $kpiLines)

$cloneContextCount = Count-Match -Lines $networkLines -Pattern "AutoStart Policy => Context=EditorClone"
$hostContextCount = Count-Match -Lines $networkLines -Pattern "AutoStart Policy => Context=EditorOriginal"
$serverStartedCount = Count-Match -Lines $networkLines -Pattern "Server started\. MaxConnections=24"
$localhostConnectCount = Count-Match -Lines $networkLines -Pattern "localhost|127\.0\.0\.1"

# Fallback evidence: KPI lines often remain when context/start logs are truncated.
$hostKpiModeCount = Count-Match -Lines $allCheckLines -Pattern "\[QA\]\[KPI\].*mode=Host"
$clientKpiModeCount = Count-Match -Lines $allCheckLines -Pattern "\[QA\]\[KPI\].*mode=Client"
$cap24EvidenceCount = Count-Match -Lines $allCheckLines -Pattern "players=\d+/24|conn=\d+/24|connMax=24|maxConnections=24|MaxConnections=24"
$hostKpiConnGe2Count = Count-KpiMetricAtLeast -Lines $kpiLines -Mode "Host" -Metric "conn" -Threshold 2
$hostKpiPlayersGe2Count = Count-KpiMetricAtLeast -Lines $kpiLines -Mode "Host" -Metric "players" -Threshold 2
$playerVsPlayerEvidenceCount = Count-PlayerVsPlayerEvidence -Lines $networkLines
$cloneFallbackFromHostConn = if ($hostKpiConnGe2Count -gt 0 -or $hostKpiPlayersGe2Count -gt 0) { 1 } else { 0 }
$cloneFallbackFromPvp = if ($playerVsPlayerEvidenceCount -gt 0) { 1 } else { 0 }

$effectiveHostContextCount = $hostContextCount + $hostKpiModeCount
$effectiveCloneContextCount = $cloneContextCount + $clientKpiModeCount + $cloneFallbackFromHostConn + $cloneFallbackFromPvp
$effectiveServerStartedCount = if ($serverStartedCount -gt 0) { $serverStartedCount } else { $cap24EvidenceCount }

$notConnectedCount = Count-Match -Lines $allCheckLines -Pattern "\[SWT-ClientSend\]: Not Connected|Not Connected"
$socketExceptionCount = Count-Match -Lines $allCheckLines -Pattern "\[SWT:Exception\]|SocketException"
$mainMenuMissingCount = Count-Match -Lines $allCheckLines -Pattern "\[MainMenuUI\] UI References are missing"
$cs0104Count = Count-Match -Lines $allCheckLines -Pattern "CS0104"
$cs0414Count = Count-Match -Lines $allCheckLines -Pattern "CS0414"

$respawnCount = Count-Match -Lines $networkLines -Pattern "CmdRespawn skipped|Respawn"
$startGameCount = Count-Match -Lines $networkLines -Pattern "StartGame"
$pvpRejectCount = Count-Match -Lines $networkLines -Pattern "\[PvP\] Kill rejected"
$pvpKillCount = Count-Match -Lines $networkLines -Pattern "\[PvP\].*(Kill|처치)"

$ignoredErrorPatterns = @(
    "EditorUpdateCheck: Failed",
    "\[Licensing::Module\]"
)

$nonEmptyErrorLines = @(
    $errorLines | Where-Object {
        if ([string]::IsNullOrWhiteSpace($_)) { return $false }

        foreach ($pattern in $ignoredErrorPatterns) {
            if ($_ -match $pattern) { return $false }
        }
        return $true
    }
)

$nonEmptyErrorCount = $nonEmptyErrorLines.Count
$nonEmptyWarningCount = @($warningLines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count

$autoRows = @(
    @{
        Item = "2) Pre-check: Console errors should be zero"
        Status = Status-FromCondition ($nonEmptyErrorCount -eq 0)
        Evidence = "errors=$nonEmptyErrorCount"
    },
    @{
        Item = "2) Pre-check: Host/Client localhost join + context logs"
        Status = Status-FromCondition ($localhostConnectCount -gt 0 -and $effectiveHostContextCount -gt 0 -and $effectiveCloneContextCount -gt 0)
        Evidence = "localhost=$localhostConnectCount, hostContext(raw/kpi/effective)=$hostContextCount/$hostKpiModeCount/$effectiveHostContextCount, cloneContext(raw/kpi+fallback/effective)=$cloneContextCount/$clientKpiModeCount+($cloneFallbackFromHostConn,$cloneFallbackFromPvp)/$effectiveCloneContextCount"
    },
    @{
        Item = "7) NET-005: Original/Clone AutoStart policy logs"
        Status = Status-FromCondition ($effectiveHostContextCount -gt 0 -and $effectiveCloneContextCount -gt 0)
        Evidence = "hostContext(raw/kpi/effective)=$hostContextCount/$hostKpiModeCount/$effectiveHostContextCount, cloneContext(raw/kpi+fallback/effective)=$cloneContextCount/$clientKpiModeCount+($cloneFallbackFromHostConn,$cloneFallbackFromPvp)/$effectiveCloneContextCount"
    },
    @{
        Item = "14) SVC-004: MaxConnections=24 applied"
        Status = Status-FromCondition ($effectiveServerStartedCount -gt 0)
        Evidence = "serverStarted(raw/effective)=$serverStartedCount/$effectiveServerStartedCount, cap24Evidence=$cap24EvidenceCount"
    },
    @{
        Item = "19) FIX-001: No CS0104 / CS0414"
        Status = Status-FromCondition ($cs0104Count -eq 0 -and $cs0414Count -eq 0)
        Evidence = "CS0104=$cs0104Count, CS0414=$cs0414Count"
    },
    @{
        Item = "10) FIX-002: MainMenuUI warning should not reappear"
        Status = Status-FromCondition ($mainMenuMissingCount -eq 0)
        Evidence = "MainMenuMissingWarning=$mainMenuMissingCount"
    },
    @{
        Item = "10) FIX-004: Not Connected / SocketException should not repeat"
        Status = Status-FromCondition ($notConnectedCount -eq 0 -and $socketExceptionCount -eq 0)
        Evidence = "NotConnected=$notConnectedCount, SocketException=$socketExceptionCount"
    }
)

$manualRows = @(
    "3) NET-001: StartGame duplication and duplicate initial spawn (log count=$startGameCount + in-game manual check)",
    "4) NET-002: Gem spawn/collect/score Host-Client consistency",
    "5) NET-003: Respawn visual and score/state sync consistency",
    "6) NET-004: PvP reject/success scenarios (reject=$pvpRejectCount, kill=$pvpKillCount)",
    "8) NET-006: Single-player fallback (MapBoundary/GameOver)",
    "9) NET-007: Spawn radius policy (GameSettings/NetworkStartPosition)",
    "10) Regression set: boost score drain / AI tunnel rule / camera zoom / minimap color"
)

$autoTableRows = @()
foreach ($row in $autoRows) {
    $autoTableRows += "| $($row.Item) | $($row.Status) | $($row.Evidence) |"
}

$cloneExamples = First-Matches -Lines $allCheckLines -Pattern "AutoStart Policy => Context=EditorClone|\[QA\]\[KPI\].*mode=Client|\[QA\]\[KPI\].*mode=Host.*(players=2|players=3|players=4|conn=2|conn=3|conn=4)|\[PvP\].*->.*(처치|Kill)" -Limit 3
$hostExamples = First-Matches -Lines $allCheckLines -Pattern "AutoStart Policy => Context=EditorOriginal|\[QA\]\[KPI\].*mode=Host" -Limit 2
$serverExamples = First-Matches -Lines $allCheckLines -Pattern "Server started\. MaxConnections=24|players=\d+/24|conn=\d+/24|connMax=24|maxConnections=24|MaxConnections=24" -Limit 2
$errorExamples = First-Matches -Lines $nonEmptyErrorLines -Pattern ".+" -Limit 3

$summary = @"
# QA-001 Log Check Summary

- SessionDir: $SessionDir
- GeneratedAt: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
- Source: `session_report.md` + `*_network.log` + `*_errors.log` + `*_warnings.log` + `*_qa_kpi.log`

## Auto Checks

| Item | Status | Evidence |
| :--- | :---: | :--- |
$($autoTableRows -join [Environment]::NewLine)

## Key Metrics

- nonEmptyErrors: $nonEmptyErrorCount
- nonEmptyWarnings: $nonEmptyWarningCount
- kpiLines: $(@($kpiLines).Count)
- serverStarted(MaxConnections=24) raw/effective: $serverStartedCount/$effectiveServerStartedCount
- cloneContext raw/kpi/fallbackConn/fallbackPvp/effective: $cloneContextCount/$clientKpiModeCount/$cloneFallbackFromHostConn/$cloneFallbackFromPvp/$effectiveCloneContextCount
- hostContext raw/kpi/effective: $hostContextCount/$hostKpiModeCount/$effectiveHostContextCount
- localhostConnect: $localhostConnectCount
- hostKpiConn>=2 evidence: $hostKpiConnGe2Count
- hostKpiPlayers>=2 evidence: $hostKpiPlayersGe2Count
- playerVsPlayerEvidence: $playerVsPlayerEvidenceCount
- respawnLogCount: $respawnCount
- startGameLogCount: $startGameCount

## Evidence Snippets

### Clone Context (sample)
$($cloneExamples | ForEach-Object { "- $_" } | Out-String)

### Host Context (sample)
$($hostExamples | ForEach-Object { "- $_" } | Out-String)

### MaxConnections=24 (sample)
$($serverExamples | ForEach-Object { "- $_" } | Out-String)

### Error Lines (sample)
$($errorExamples | ForEach-Object { "- $_" } | Out-String)

## Manual Checks Remaining
$($manualRows | ForEach-Object { "- [ ] $_" } | Out-String)

## Gate

- QA-001 final close condition: checklist items 2)~10) manual verification complete + failures recorded as issues.
- Current decision: **HOLD** (manual verification still pending).
"@

$outDir = Split-Path -Path $OutputPath -Parent
if (-not [string]::IsNullOrWhiteSpace($outDir) -and -not (Test-Path -Path $outDir -PathType Container)) {
    New-Item -Path $outDir -ItemType Directory -Force | Out-Null
}

Set-Content -Path $OutputPath -Encoding UTF8 -Value $summary
Write-Output "QA-001 log check summary written: $OutputPath"
