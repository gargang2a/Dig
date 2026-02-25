param(
    [Parameter(Mandatory = $true)]
    [string]$SessionDir,

    [string]$SummaryPath,

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -Path $SessionDir -PathType Container)) {
    throw "SessionDir not found: $SessionDir"
}

if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    $SummaryPath = Join-Path -Path $SessionDir -ChildPath "qa001_log_check_summary.md"
}

if (-not (Test-Path -Path $SummaryPath -PathType Leaf)) {
    throw "SummaryPath not found: $SummaryPath"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path -Path $SessionDir -ChildPath "qa_release_note_block.md"
}

function Read-Lines {
    param([string]$Path)

    $raw = Get-Content -Path $Path -Encoding UTF8
    if ($null -eq $raw) { return @() }
    if ($raw -is [string]) { return @($raw) }
    return @($raw)
}

function Get-MatchValue {
    param(
        [string[]]$Lines,
        [string]$Pattern,
        [int]$Group = 1,
        [string]$Default = "n/a"
    )

    foreach ($line in $Lines) {
        if ($line -match $Pattern) {
            return $Matches[$Group]
        }
    }

    return $Default
}

$lines = @(Read-Lines -Path $SummaryPath)

$sessionId = Split-Path -Path (Resolve-Path -Path $SessionDir).Path -Leaf
$generatedAt = Get-MatchValue -Lines $lines -Pattern "^- GeneratedAt:\s*(.+)$"
$nonEmptyErrors = Get-MatchValue -Lines $lines -Pattern "^- nonEmptyErrors:\s*(\d+)$"
$ignoredErrors = Get-MatchValue -Lines $lines -Pattern "^- ignoredErrors:\s*(\d+)$"
$notConnected = Get-MatchValue -Lines $lines -Pattern "NotConnected=(\d+),\s*SocketException=(\d+)" -Group 1 -Default "n/a"
$socketException = Get-MatchValue -Lines $lines -Pattern "NotConnected=(\d+),\s*SocketException=(\d+)" -Group 2 -Default "n/a"
$gateDecision = Get-MatchValue -Lines $lines -Pattern "^- Current decision:\s*\*\*(.+?)\*\*$"
$gateReason = Get-MatchValue -Lines $lines -Pattern "^- Gate reason:\s*(.+)$"

$autoPassCount = @($lines | Where-Object { $_ -match "^\|\s.+\|\sAUTO_PASS\s\|" }).Count
$autoFailCount = @($lines | Where-Object { $_ -match "^\|\s.+\|\sAUTO_FAIL\s\|" }).Count
$manualCheckedCount = @($lines | Where-Object { $_ -match "^- \[x\]\s" }).Count
$manualPendingCount = @($lines | Where-Object { $_ -match "^- \[ \]\s" }).Count

$manualState = "pending"
if ($manualPendingCount -eq 0 -and $manualCheckedCount -gt 0) {
    $manualState = "done"
}

$snippet = "- QA session '$sessionId' summary: AutoChecks PASS=$autoPassCount/FAIL=$autoFailCount, nonEmptyErrors=$nonEmptyErrors (ignored=$ignoredErrors), NotConnected=$notConnected, SocketException=$socketException, Gate=$gateDecision."
if ($gateReason -ne "n/a") {
    $snippet = "$snippet (reason: $gateReason)"
}

$content = @"
# QA Release Note Block

- SessionId: $sessionId
- SummaryPath: $SummaryPath
- GeneratedAt(summary): $generatedAt
- ManualChecks: $manualState (checked=$manualCheckedCount, pending=$manualPendingCount)
- Gate: $gateDecision

## Release Snippet

$snippet
"@

$outputDir = Split-Path -Path $OutputPath -Parent
if (-not [string]::IsNullOrWhiteSpace($outputDir) -and -not (Test-Path -Path $outputDir -PathType Container)) {
    New-Item -Path $outputDir -ItemType Directory -Force | Out-Null
}

Set-Content -Path $OutputPath -Encoding UTF8 -Value $content
Write-Output "QA release note block written: $OutputPath"
