param(
    [string]$StackStatePath = ".\build\ops\friend_test_stack_state.json",
    [string]$HealthScriptPath = ".\Tools\ops\check_server_runtime_health.ps1",
    [string]$HealthOutputDir = ".\build\ops\health",
    [string]$OutputDir = ".\build\ops\health\soak",
    [int]$DurationMinutes = 10,
    [int]$PollSeconds = 30,
    [int]$BotIndexSpikeMultiplier = 4,
    [int]$MaxAllowedSandworms = 3
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        New-Item -Path $Path -ItemType Directory | Out-Null
    }
}

function Get-LatestReportFile {
    param(
        [Parameter(Mandatory = $true)][string]$DirectoryPath,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    if (-not (Test-Path -LiteralPath $DirectoryPath -PathType Container)) {
        return $null
    }

    return Get-ChildItem -LiteralPath $DirectoryPath -Filter $Pattern -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

if ($DurationMinutes -lt 1) {
    throw "DurationMinutes must be >= 1."
}

if ($PollSeconds -lt 5) {
    throw "PollSeconds must be >= 5."
}

$stackStatePathAbs = Resolve-AbsolutePath -Path $StackStatePath
$healthScriptPathAbs = Resolve-AbsolutePath -Path $HealthScriptPath
$healthOutputDirAbs = Resolve-AbsolutePath -Path $HealthOutputDir
$outputDirAbs = Resolve-AbsolutePath -Path $OutputDir

if (-not (Test-Path -LiteralPath $healthScriptPathAbs -PathType Leaf)) {
    throw "Health script not found: $healthScriptPathAbs"
}

Ensure-Directory -Path $healthOutputDirAbs
Ensure-Directory -Path $outputDirAbs

$serverReportPath = ""
if (Test-Path -LiteralPath $stackStatePathAbs -PathType Leaf) {
    $state = Read-JsonFile -Path $stackStatePathAbs
    if ($null -ne $state -and $null -ne $state.server) {
        $candidate = [string]$state.server.reportPath
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $serverReportPath = $candidate
        }
    }
}

if ([string]::IsNullOrWhiteSpace($serverReportPath)) {
    $latestServerReport = Get-LatestReportFile -DirectoryPath (Resolve-AbsolutePath -Path ".\build\ops\server") -Pattern "dedicated_server_*.json"
    if ($null -eq $latestServerReport) {
        throw "Unable to resolve server report path. Run friend stack start/status first."
    }
    $serverReportPath = $latestServerReport.FullName
}

$startedAt = Get-Date
$deadline = $startedAt.AddMinutes($DurationMinutes)
$samples = New-Object System.Collections.Generic.List[object]
$allNotes = New-Object System.Collections.Generic.List[string]

Write-Host "[Soak] Started: $($startedAt.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))"
Write-Host "[Soak] DurationMinutes=$DurationMinutes, PollSeconds=$PollSeconds"
Write-Host "[Soak] ServerReportPath=$serverReportPath"

while ($true) {
    & $healthScriptPathAbs `
        -ServerReportPath $serverReportPath `
        -OutputDir $healthOutputDirAbs `
        -BotIndexSpikeMultiplier $BotIndexSpikeMultiplier `
        -MaxAllowedSandworms $MaxAllowedSandworms | Out-Null

    $latestHealthJson = Get-LatestReportFile -DirectoryPath $healthOutputDirAbs -Pattern "server_runtime_health_*.json"
    if ($null -eq $latestHealthJson) {
        throw "Health report was not generated."
    }

    $health = Read-JsonFile -Path $latestHealthJson.FullName
    $sample = [pscustomobject]@{
        timestampUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        status = [string]$health.status
        botTargetCount = [int]$health.botTargetCount
        maxBotIndexSeen = [int]$health.maxBotIndexSeen
        sandwormSpawnCount = [int]$health.sandwormSpawnCount
        reportPath = $latestHealthJson.FullName
    }
    $samples.Add($sample)

    foreach ($note in @($health.notes)) {
        if ([string]::IsNullOrWhiteSpace([string]$note)) { continue }
        $allNotes.Add([string]$note)
    }

    Write-Host ("[Soak] Sample#{0}: status={1}, botTarget={2}, maxBotIndex={3}, sandworm={4}" -f `
        $samples.Count, $sample.status, $sample.botTargetCount, $sample.maxBotIndexSeen, $sample.sandwormSpawnCount)

    if ((Get-Date) -ge $deadline) {
        break
    }

    Start-Sleep -Seconds $PollSeconds
}

$passCount = @($samples | Where-Object { $_.status -eq "PASS" }).Count
$checkCount = @($samples | Where-Object { $_.status -ne "PASS" }).Count
$maxObservedBotIndex = -1
$maxObservedSandworm = -1

if ($samples.Count -gt 0) {
    $maxObservedBotIndex = (@($samples | Measure-Object -Property maxBotIndexSeen -Maximum).Maximum)
    $maxObservedSandworm = (@($samples | Measure-Object -Property sandwormSpawnCount -Maximum).Maximum)
}

$overallStatus = if ($samples.Count -gt 0 -and $checkCount -eq 0) { "PASS" } else { "CHECK" }
$uniqueNotes = @($allNotes | Sort-Object -Unique)

$endedAt = Get-Date
$timestamp = $endedAt.ToUniversalTime().ToString("yyyyMMdd_HHmmss")
$summaryJsonPath = Join-Path $outputDirAbs ("server_health_soak_" + $timestamp + ".json")
$summaryMdPath = [System.IO.Path]::ChangeExtension($summaryJsonPath, ".md")

$summary = [ordered]@{
    createdAtUtc = $endedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    startedAtUtc = $startedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    endedAtUtc = $endedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    durationMinutes = $DurationMinutes
    pollSeconds = $PollSeconds
    serverReportPath = $serverReportPath
    sampleCount = $samples.Count
    passCount = $passCount
    checkCount = $checkCount
    maxObservedBotIndex = $maxObservedBotIndex
    maxObservedSandwormSpawnCount = $maxObservedSandworm
    overallStatus = $overallStatus
    notes = $uniqueNotes
    samples = $samples
}

$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryJsonPath -Encoding UTF8

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Server Health Soak Summary")
$md.Add("")
$md.Add("- CreatedAtUtc: $($summary.createdAtUtc)")
$md.Add("- StartedAtUtc: $($summary.startedAtUtc)")
$md.Add("- EndedAtUtc: $($summary.endedAtUtc)")
$md.Add("- DurationMinutes: $($summary.durationMinutes)")
$md.Add("- PollSeconds: $($summary.pollSeconds)")
$md.Add("- ServerReportPath: $($summary.serverReportPath)")
$md.Add("- SampleCount: $($summary.sampleCount)")
$md.Add("- PASS Samples: $($summary.passCount)")
$md.Add("- CHECK Samples: $($summary.checkCount)")
$md.Add("- MaxObservedBotIndex: $($summary.maxObservedBotIndex)")
$md.Add("- MaxObservedSandwormSpawnCount: $($summary.maxObservedSandwormSpawnCount)")
$md.Add("- OverallStatus: $($summary.overallStatus)")
$md.Add("")

if ($summary.notes.Count -gt 0) {
    $md.Add("## Notes")
    $md.Add("")
    foreach ($note in $summary.notes) {
        $md.Add("- $note")
    }
    $md.Add("")
}

$md.Add("## Samples")
$md.Add("")
$md.Add("| # | TimestampUtc | Status | BotTarget | MaxBotIndex | SandwormSpawnCount |")
$md.Add("| :---: | :--- | :---: | ---: | ---: | ---: |")

for ($i = 0; $i -lt $samples.Count; $i++) {
    $s = $samples[$i]
    $md.Add("| $($i + 1) | $($s.timestampUtc) | $($s.status) | $($s.botTargetCount) | $($s.maxBotIndexSeen) | $($s.sandwormSpawnCount) |")
}

$md -join "`n" | Set-Content -LiteralPath $summaryMdPath -Encoding UTF8

Write-Host "[Soak] OverallStatus=$overallStatus, samples=$($samples.Count), pass=$passCount, check=$checkCount"
Write-Host "[Soak] MaxObservedBotIndex=$maxObservedBotIndex, MaxObservedSandwormSpawnCount=$maxObservedSandworm"
Write-Host "[Soak] Summary(MD): $summaryMdPath"
Write-Host "[Soak] Summary(JSON): $summaryJsonPath"
