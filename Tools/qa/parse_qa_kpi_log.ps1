param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -Path $InputPath -PathType Leaf)) {
    throw "InputPath not found: $InputPath"
}

function Get-RegexValue {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$GroupName,
        [Parameter(Mandatory = $true)][object]$DefaultValue
    )

    $m = [regex]::Match($Text, $Pattern)
    if (-not $m.Success) { return $DefaultValue }
    return $m.Groups[$GroupName].Value
}

function To-Double {
    param([object]$Value)
    if ($null -eq $Value) { return 0.0 }

    $number = 0.0
    if ([double]::TryParse($Value.ToString(), [ref]$number)) {
        return $number
    }

    return 0.0
}

function To-Int {
    param([object]$Value)
    if ($null -eq $Value) { return 0 }

    $number = 0
    if ([int]::TryParse($Value.ToString(), [ref]$number)) {
        return $number
    }

    return 0
}

$rawLines = Get-Content -Path $InputPath -Encoding UTF8
$qaLines = $rawLines | Where-Object { $_ -match "\[QA\]\[KPI\]\[" }

$samples = @()
foreach ($line in $qaLines) {
    $reason = Get-RegexValue -Text $line -Pattern "\[QA\]\[KPI\]\[(?<reason>[^\]]+)\]" -GroupName "reason" -DefaultValue ""
    if ([string]::IsNullOrWhiteSpace($reason)) { continue }

    $mode = Get-RegexValue -Text $line -Pattern "mode=(?<mode>\S+)" -GroupName "mode" -DefaultValue "Unknown"
    $duration = Get-RegexValue -Text $line -Pattern "duration=(?<duration>\d{2}:\d{2})" -GroupName "duration" -DefaultValue "00:00"
    $quality = Get-RegexValue -Text $line -Pattern "quality=(?<quality>\S+)" -GroupName "quality" -DefaultValue "N/A"
    $qa = Get-RegexValue -Text $line -Pattern "qa=(?<qa>\S+)$" -GroupName "qa" -DefaultValue "UNKNOWN"

    $currentPlayers = To-Int (Get-RegexValue -Text $line -Pattern "players=(?<current>\d+)\/(?<cap>\d+)" -GroupName "current" -DefaultValue 0)
    $hardCap = To-Int (Get-RegexValue -Text $line -Pattern "players=(?<current>\d+)\/(?<cap>\d+)" -GroupName "cap" -DefaultValue 0)
    $connections = To-Int (Get-RegexValue -Text $line -Pattern "conn=(?<conn>\d+)\s+connMax=(?<connMax>\d+)" -GroupName "conn" -DefaultValue 0)
    $connMax = To-Int (Get-RegexValue -Text $line -Pattern "conn=(?<conn>\d+)\s+connMax=(?<connMax>\d+)" -GroupName "connMax" -DefaultValue 0)
    $tickActual = To-Int (Get-RegexValue -Text $line -Pattern "tick=(?<actual>\d+)\/(?<target>\d+)" -GroupName "actual" -DefaultValue 0)
    $tickTarget = To-Int (Get-RegexValue -Text $line -Pattern "tick=(?<actual>\d+)\/(?<target>\d+)" -GroupName "target" -DefaultValue 0)

    $rttCur = To-Double (Get-RegexValue -Text $line -Pattern "rtt\(cur\/avg\/p95\/max\)=(?<cur>[\d\.]+)\/(?<avg>[\d\.]+)\/(?<p95>[\d\.]+)\/(?<max>[\d\.]+)ms" -GroupName "cur" -DefaultValue 0)
    $rttAvg = To-Double (Get-RegexValue -Text $line -Pattern "rtt\(cur\/avg\/p95\/max\)=(?<cur>[\d\.]+)\/(?<avg>[\d\.]+)\/(?<p95>[\d\.]+)\/(?<max>[\d\.]+)ms" -GroupName "avg" -DefaultValue 0)
    $rttP95 = To-Double (Get-RegexValue -Text $line -Pattern "rtt\(cur\/avg\/p95\/max\)=(?<cur>[\d\.]+)\/(?<avg>[\d\.]+)\/(?<p95>[\d\.]+)\/(?<max>[\d\.]+)ms" -GroupName "p95" -DefaultValue 0)
    $rttMax = To-Double (Get-RegexValue -Text $line -Pattern "rtt\(cur\/avg\/p95\/max\)=(?<cur>[\d\.]+)\/(?<avg>[\d\.]+)\/(?<p95>[\d\.]+)\/(?<max>[\d\.]+)ms" -GroupName "max" -DefaultValue 0)

    $frameNow = To-Double (Get-RegexValue -Text $line -Pattern "serverFrame\(now\/avg\/max\)=(?<now>[\d\.]+)\/(?<avg>[\d\.]+)\/(?<max>[\d\.]+)ms" -GroupName "now" -DefaultValue 0)
    $frameAvg = To-Double (Get-RegexValue -Text $line -Pattern "serverFrame\(now\/avg\/max\)=(?<now>[\d\.]+)\/(?<avg>[\d\.]+)\/(?<max>[\d\.]+)ms" -GroupName "avg" -DefaultValue 0)
    $frameMax = To-Double (Get-RegexValue -Text $line -Pattern "serverFrame\(now\/avg\/max\)=(?<now>[\d\.]+)\/(?<avg>[\d\.]+)\/(?<max>[\d\.]+)ms" -GroupName "max" -DefaultValue 0)

    $serverRttAvg = To-Double (Get-RegexValue -Text $line -Pattern "serverRtt\(avgNow\/maxSample\)=(?<avg>[\d\.]+)\/(?<max>[\d\.]+)ms" -GroupName "avg" -DefaultValue 0)
    $serverRttMax = To-Double (Get-RegexValue -Text $line -Pattern "serverRtt\(avgNow\/maxSample\)=(?<avg>[\d\.]+)\/(?<max>[\d\.]+)ms" -GroupName "max" -DefaultValue 0)

    $samples += [PSCustomObject]@{
        Reason          = $reason
        Mode            = $mode
        Duration        = $duration
        CurrentPlayers  = $currentPlayers
        HardCap         = $hardCap
        Connections     = $connections
        ConnMax         = $connMax
        TickActual      = $tickActual
        TickTarget      = $tickTarget
        RttCur          = $rttCur
        RttAvg          = $rttAvg
        RttP95          = $rttP95
        RttMax          = $rttMax
        ServerFrameNow  = $frameNow
        ServerFrameAvg  = $frameAvg
        ServerFrameMax  = $frameMax
        ServerRttAvg    = $serverRttAvg
        ServerRttMax    = $serverRttMax
        Quality         = $quality
        Qa              = $qa
        Raw             = $line
    }
}

if (@($samples).Count -eq 0) {
    throw "No [QA][KPI] lines found in input file: $InputPath"
}

$sampleCount = @($samples).Count
$p95Avg = ($samples | Measure-Object -Property RttP95 -Average).Average
$p95Max = ($samples | Measure-Object -Property RttP95 -Maximum).Maximum
$frameNowAvg = ($samples | Measure-Object -Property ServerFrameNow -Average).Average
$frameNowMax = ($samples | Measure-Object -Property ServerFrameNow -Maximum).Maximum
$maxPlayers = ($samples | Measure-Object -Property CurrentPlayers -Maximum).Maximum
$maxConn = ($samples | Measure-Object -Property ConnMax -Maximum).Maximum
$passCount = @($samples | Where-Object { $_.Qa -eq "PASS" }).Count
$checkCount = @($samples | Where-Object { $_.Qa -ne "PASS" }).Count
$rttViolations = @($samples | Where-Object { $_.RttP95 -gt 150 }).Count
$frameViolations = @($samples | Where-Object { $_.ServerFrameNow -gt 25 }).Count
$latest = @($samples)[@($samples).Count - 1]

$generatedAt = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $baseDir = Split-Path -Path $InputPath -Parent
    if ([string]::IsNullOrWhiteSpace($baseDir)) { $baseDir = "." }
    $stamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
    $OutputPath = Join-Path -Path $baseDir -ChildPath "qa_kpi_summary_$stamp.md"
}

$rawTail = ($samples | Select-Object -Last 5 | ForEach-Object { "- " + $_.Raw }) -join [Environment]::NewLine

$summary = @"
# QA KPI Summary

- GeneratedAt: $generatedAt
- Source: $InputPath
- SampleCount: $sampleCount

## Aggregate

| Metric | Value |
| :--- | :--- |
| p95 RTT avg / max | $([math]::Round($p95Avg, 1)) / $([math]::Round($p95Max, 1)) ms |
| ServerFrame(now) avg / max | $([math]::Round($frameNowAvg, 2)) / $([math]::Round($frameNowMax, 2)) ms |
| Max Players | $maxPlayers / $($latest.HardCap) |
| Max Connections (connMax) | $maxConn |
| QA PASS / CHECK | $passCount / $checkCount |
| p95 RTT violations (>150ms) | $rttViolations |
| ServerFrame(now) violations (>25ms) | $frameViolations |

## Latest Snapshot

- Reason: $($latest.Reason)
- Mode: $($latest.Mode)
- Duration: $($latest.Duration)
- Players: $($latest.CurrentPlayers)/$($latest.HardCap)
- Connections: $($latest.Connections) (connMax=$($latest.ConnMax))
- Tick: $($latest.TickActual)/$($latest.TickTarget)
- RTT(cur/avg/p95/max): $($latest.RttCur)/$($latest.RttAvg)/$($latest.RttP95)/$($latest.RttMax) ms
- ServerFrame(now/avg/max): $($latest.ServerFrameNow)/$($latest.ServerFrameAvg)/$($latest.ServerFrameMax) ms
- ServerRtt(avg/max): $($latest.ServerRttAvg)/$($latest.ServerRttMax) ms
- Quality: $($latest.Quality)
- QA: $($latest.Qa)

## Raw Samples (last 5)
$rawTail
"@

$outDir = Split-Path -Path $OutputPath -Parent
if (-not [string]::IsNullOrWhiteSpace($outDir) -and -not (Test-Path -Path $outDir)) {
    New-Item -Path $outDir -ItemType Directory -Force | Out-Null
}

Set-Content -Path $OutputPath -Encoding UTF8 -Value $summary
Write-Output "QA KPI summary written: $OutputPath"
