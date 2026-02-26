param(
    [string]$UnityLogPath = "",
    [string]$ServerReportPath = "",
    [string]$OutputDir = ".\build\ops\health",
    [int]$BotIndexSpikeMultiplier = 4,
    [int]$BotDistinctSpikeMultiplier = 24,
    [int]$BotIndexHardCheckThreshold = 512,
    [int]$MaxAllowedSandworms = 3,
    [switch]$UseLatestServerSessionScope
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$BOT_INDEX_ABSOLUTE_CHECK_THRESHOLD = 128
$BOT_DISTINCT_ABSOLUTE_CHECK_THRESHOLD = 192

function Resolve-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-LatestServerUnityLog {
    param([Parameter(Mandatory = $true)][string]$ServerOutDir)

    if (-not (Test-Path -LiteralPath $ServerOutDir -PathType Container)) {
        return $null
    }

    return Get-ChildItem -LiteralPath $ServerOutDir -Filter "dedicated_server_*_unity.log" -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function New-MarkdownReport {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][hashtable]$Result
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Server Runtime Health")
    $lines.Add("")
    $lines.Add("- CreatedAtUtc: $($Result.createdAtUtc)")
    $lines.Add("- Status: $($Result.status)")
    $lines.Add("- UnityLogPath: $($Result.unityLogPath)")
    $lines.Add("- AnalysisScope: $($Result.analysisScope)")
    $lines.Add("- ScopeStartCharIndex: $($Result.scopeStartCharIndex)")
    $lines.Add("- ServerStartedCountTotal: $($Result.serverStartedCountTotal)")
    $lines.Add("- ServerStartedCount: $($Result.serverStartedCount)")
    $lines.Add("- BotTargetCount: $($Result.botTargetCount)")
    $lines.Add("- MaxBotIndexSeen: $($Result.maxBotIndexSeen)")
    $lines.Add("- DistinctBotNamesSeen: $($Result.distinctBotNamesSeen)")
    $lines.Add("- BotIndexSpikeThreshold: $($Result.botIndexSpikeThreshold)")
    $lines.Add("- BotDistinctSpikeThreshold: $($Result.botDistinctSpikeThreshold)")
    $lines.Add("- BotIndexHardCheckThreshold: $($Result.botIndexHardCheckThreshold)")
    $lines.Add("- SandwormSpawnCount: $($Result.sandwormSpawnCount)")
    $lines.Add("- MaxSandwormIndexSeen: $($Result.maxSandwormIndexSeen)")
    $lines.Add("- DuplicateSpawnerDetected: $($Result.duplicateSpawnerDetected)")
    $lines.Add("- BotCapEnforcedLogSeen: $($Result.botCapEnforcedLogSeen)")
    $lines.Add("- SandwormCapEnforcedLogSeen: $($Result.sandwormCapEnforcedLogSeen)")
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

if ($BotIndexSpikeMultiplier -lt 2) {
    throw "BotIndexSpikeMultiplier must be >= 2."
}

if ($BotDistinctSpikeMultiplier -lt 4) {
    throw "BotDistinctSpikeMultiplier must be >= 4."
}

if ($BotIndexHardCheckThreshold -lt $BOT_INDEX_ABSOLUTE_CHECK_THRESHOLD) {
    throw "BotIndexHardCheckThreshold must be >= $BOT_INDEX_ABSOLUTE_CHECK_THRESHOLD."
}

if ($MaxAllowedSandworms -lt 1) {
    throw "MaxAllowedSandworms must be >= 1."
}

$serverReport = $null
if (-not [string]::IsNullOrWhiteSpace($ServerReportPath)) {
    $serverReportPathAbs = Resolve-AbsolutePath -Path $ServerReportPath
    if (-not (Test-Path -LiteralPath $serverReportPathAbs -PathType Leaf)) {
        throw "ServerReportPath not found: $serverReportPathAbs"
    }
    $serverReport = Read-JsonFile -Path $serverReportPathAbs
}

if ([string]::IsNullOrWhiteSpace($UnityLogPath)) {
    if ($null -ne $serverReport -and -not [string]::IsNullOrWhiteSpace([string]$serverReport.unityLogPath)) {
        $UnityLogPath = [string]$serverReport.unityLogPath
    } else {
        $serverOutDir = Resolve-AbsolutePath -Path ".\build\ops\server"
        $latest = Get-LatestServerUnityLog -ServerOutDir $serverOutDir
        if ($null -eq $latest) {
            throw "Unable to resolve Unity log path. Provide -UnityLogPath or -ServerReportPath."
        }
        $UnityLogPath = $latest.FullName
    }
}

$unityLogPathAbs = Resolve-AbsolutePath -Path $UnityLogPath
if (-not (Test-Path -LiteralPath $unityLogPathAbs -PathType Leaf)) {
    throw "UnityLogPath not found: $unityLogPathAbs"
}

$outputDirAbs = Resolve-AbsolutePath -Path $OutputDir
if (-not (Test-Path -LiteralPath $outputDirAbs -PathType Container)) {
    New-Item -Path $outputDirAbs -ItemType Directory | Out-Null
}

$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd_HHmmss")
$reportMd = Join-Path $outputDirAbs ("server_runtime_health_" + $timestamp + ".md")
$reportJson = [System.IO.Path]::ChangeExtension($reportMd, ".json")

$content = Get-Content -LiteralPath $unityLogPathAbs -Raw

$serverStartPattern = "\[Network\] Server started\. MaxConnections="
$serverStartMatchesTotal = [regex]::Matches($content, $serverStartPattern)
$serverStartedCountTotal = $serverStartMatchesTotal.Count

$analysisContent = $content
$analysisScope = "full-log"
$scopeStartCharIndex = 0

$useScopedAnalysis = $UseLatestServerSessionScope.IsPresent
if (-not $UseLatestServerSessionScope.IsPresent) {
    $useScopedAnalysis = $true
}

if ($useScopedAnalysis -and $serverStartedCountTotal -gt 0) {
    $lastServerStartMatch = $serverStartMatchesTotal[$serverStartedCountTotal - 1]
    $scopeStartCharIndex = [Math]::Max(0, $lastServerStartMatch.Index)
    if ($scopeStartCharIndex -gt 0) {
        $analysisContent = $content.Substring($scopeStartCharIndex)
    }
    $analysisScope = "latest-server-session"
}

$serverStartedCount = [regex]::Matches($analysisContent, $serverStartPattern).Count

$botTargetCount = 0
$botTargetMatches = [regex]::Matches($analysisContent, "\[BotSpawner\].*?(?<count>\d+)\s*留덈━")
if ($botTargetMatches.Count -eq 0) {
    $botTargetMatches = [regex]::Matches($analysisContent, "\[BotSpawner\].*?(?<count>\d+)\s*bots?")
}
if ($botTargetMatches.Count -gt 0) {
    $botTargetCount = [int]$botTargetMatches[$botTargetMatches.Count - 1].Groups["count"].Value
}

$botIndexValues = New-Object System.Collections.Generic.List[int]
$botIndexMatches = [regex]::Matches($analysisContent, "\bBot_(?<idx>\d+)\b")
foreach ($m in $botIndexMatches) {
    $botIndexValues.Add([int]$m.Groups["idx"].Value)
}

$maxBotIndexSeen = -1
$distinctBotNamesSeen = 0
if ($botIndexValues.Count -gt 0) {
    $maxBotIndexSeen = ($botIndexValues | Measure-Object -Maximum).Maximum
    $distinctBotNamesSeen = (@($botIndexValues | Sort-Object -Unique)).Count
}

$botIndexSpikeThreshold = 0
if ($botTargetCount -gt 0) {
    $botIndexSpikeThreshold = [Math]::Max($BOT_INDEX_ABSOLUTE_CHECK_THRESHOLD, ($botTargetCount * $BotIndexSpikeMultiplier))
} else {
    $botIndexSpikeThreshold = $BOT_INDEX_ABSOLUTE_CHECK_THRESHOLD
}

$botDistinctSpikeThreshold = 0
if ($botTargetCount -gt 0) {
    $botDistinctSpikeThreshold = [Math]::Max($BOT_DISTINCT_ABSOLUTE_CHECK_THRESHOLD, ($botTargetCount * $BotDistinctSpikeMultiplier))
} else {
    $botDistinctSpikeThreshold = $BOT_DISTINCT_ABSOLUTE_CHECK_THRESHOLD
}

$sandwormSpawnMatches = [regex]::Matches($analysisContent, "\[SandwormManager\]\s+Spawned Sandworm_(?<idx>\d+)")
$sandwormSpawnCount = $sandwormSpawnMatches.Count
$maxSandwormIndexSeen = -1
if ($sandwormSpawnMatches.Count -gt 0) {
    $sandwormIndexes = @()
    foreach ($m in $sandwormSpawnMatches) { $sandwormIndexes += [int]$m.Groups["idx"].Value }
    $maxSandwormIndexSeen = ($sandwormIndexes | Measure-Object -Maximum).Maximum
}

$duplicateSpawnerDetected = $analysisContent.Contains("Duplicate server spawner detected")
$botCapEnforcedLogSeen = $analysisContent.Contains("[BotSpawner] Enforcing Free-MVP bot cap")
$sandwormCapEnforcedLogSeen = $analysisContent.Contains("[SandwormManager] Enforcing Free-MVP sandworm cap")

$notes = New-Object System.Collections.Generic.List[string]
$status = "PASS"

if ($serverStartedCount -eq 0) {
    $status = "CHECK"
    $notes.Add("Server started log not found.")
}
if ($analysisScope -eq "latest-server-session" -and $serverStartedCountTotal -gt $serverStartedCount) {
    $notes.Add("Scoped analysis to latest server session to avoid stale historical log noise.")
}

if ($duplicateSpawnerDetected) {
    $status = "CHECK"
    $notes.Add("Duplicate server spawner was detected.")
}

$botIndexAboveThreshold = ($maxBotIndexSeen -ge $botIndexSpikeThreshold -and $botIndexSpikeThreshold -gt 0)
$botDistinctAboveThreshold = ($distinctBotNamesSeen -ge $botDistinctSpikeThreshold -and $botDistinctSpikeThreshold -gt 0)
$botIndexAboveHardThreshold = ($maxBotIndexSeen -ge $BotIndexHardCheckThreshold)

if (($botIndexAboveThreshold -and $botDistinctAboveThreshold) -or $botIndexAboveHardThreshold) {
    $status = "CHECK"
    if ($botIndexAboveHardThreshold) {
        $notes.Add("Bot index hard-threshold exceeded: maxBotIndex=$maxBotIndexSeen, hardThreshold=$BotIndexHardCheckThreshold.")
    } else {
        $notes.Add("Bot index/distinct spike detected: maxBotIndex=$maxBotIndexSeen (threshold=$botIndexSpikeThreshold), distinct=$distinctBotNamesSeen (threshold=$botDistinctSpikeThreshold), target=$botTargetCount.")
    }
}
elseif ($botIndexAboveThreshold -and -not $botDistinctAboveThreshold) {
    $notes.Add("High bot index observed but distinct bot count is below spike threshold (index=$maxBotIndexSeen/$botIndexSpikeThreshold, distinct=$distinctBotNamesSeen/$botDistinctSpikeThreshold). Treated as churn, not spike.")
}

if ($sandwormSpawnCount -gt $MaxAllowedSandworms) {
    $status = "CHECK"
    $notes.Add("Sandworm spawn count exceeded expected cap: count=$sandwormSpawnCount, expected<=$MaxAllowedSandworms.")
    $notes.Add("Possible stale dedicated-server binary/config. Rebuild dedicated server and restart stack.")
}

$result = [ordered]@{
    createdAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    status = $status
    unityLogPath = $unityLogPathAbs
    analysisScope = $analysisScope
    scopeStartCharIndex = $scopeStartCharIndex
    serverStartedCountTotal = $serverStartedCountTotal
    serverStartedCount = $serverStartedCount
    botTargetCount = $botTargetCount
    maxBotIndexSeen = $maxBotIndexSeen
    distinctBotNamesSeen = $distinctBotNamesSeen
    botIndexSpikeThreshold = $botIndexSpikeThreshold
    botDistinctSpikeThreshold = $botDistinctSpikeThreshold
    botIndexHardCheckThreshold = $BotIndexHardCheckThreshold
    sandwormSpawnCount = $sandwormSpawnCount
    maxSandwormIndexSeen = $maxSandwormIndexSeen
    duplicateSpawnerDetected = $duplicateSpawnerDetected
    botCapEnforcedLogSeen = $botCapEnforcedLogSeen
    sandwormCapEnforcedLogSeen = $sandwormCapEnforcedLogSeen
    notes = $notes
}

New-MarkdownReport -Path $reportMd -Result $result
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportJson -Encoding UTF8

Write-Host "[Health] Status: $status"
Write-Host "[Health] Scope: $analysisScope (startChar=$scopeStartCharIndex, serverStartsTotal=$serverStartedCountTotal)"
Write-Host "[Health] Bot: target=$botTargetCount, maxIndex=$maxBotIndexSeen, threshold=$botIndexSpikeThreshold, distinct=$distinctBotNamesSeen/$botDistinctSpikeThreshold, hard=$BotIndexHardCheckThreshold"
Write-Host "[Health] Sandworm: spawnCount=$sandwormSpawnCount, maxAllowed=$MaxAllowedSandworms"
Write-Host "[Health] Report(MD): $reportMd"
Write-Host "[Health] Report(JSON): $reportJson"

