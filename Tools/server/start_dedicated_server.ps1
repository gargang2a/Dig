param(
    [Parameter(Mandatory = $true)]
    [string]$ServerBuildPath,

    [int]$Port = 7778,

    [string]$OutputDir = ".\build\server-runtime",

    [string]$AdditionalArgs = "",

    [int]$StartupWaitSeconds = 8,

    [switch]$DisableLayoutAutoRepair
)

$ErrorActionPreference = "Stop"

function Resolve-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function New-MarkdownReport {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][hashtable]$Result
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Dedicated Server Runtime Report")
    $lines.Add("")
    $lines.Add("- CreatedAtUtc: $($Result.createdAtUtc)")
    $lines.Add("- Status: $($Result.status)")
    $lines.Add("- ServerBuildPath: $($Result.serverBuildPath)")
    $lines.Add("- ServerPid: $($Result.serverPid)")
    $lines.Add("- Port: $($Result.port)")
    $lines.Add("- PortOpen: $($Result.portOpen)")
    $lines.Add("- LocalEndpoint: $($Result.localEndpoint)")
    $lines.Add("- UnityLogPath: $($Result.unityLogPath)")
    $lines.Add("- StdOutPath: $($Result.stdoutPath)")
    $lines.Add("- StdErrPath: $($Result.stderrPath)")
    $lines.Add("- CmdLine: $($Result.commandLine)")
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

function Copy-DirectoryReplace {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    if (Test-Path -LiteralPath $DestinationPath -PathType Container) {
        Remove-Item -LiteralPath $DestinationPath -Recurse -Force
    }

    Copy-Item -Path $SourcePath -Destination $DestinationPath -Recurse -Force
}

function Get-FirstExistingDirectory {
    param(
        [Parameter(Mandatory = $true)][string[]]$Candidates,
        [string]$ExcludePath = ""
    )

    $excludeAbs = ""
    if (-not [string]::IsNullOrWhiteSpace($ExcludePath)) {
        $excludeAbs = [System.IO.Path]::GetFullPath($ExcludePath)
    }

    foreach ($candidate in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $candidateAbs = [System.IO.Path]::GetFullPath($candidate)
        if (-not [string]::IsNullOrWhiteSpace($excludeAbs) -and
            [string]::Equals($candidateAbs, $excludeAbs, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        if (Test-Path -LiteralPath $candidateAbs -PathType Container) {
            return $candidateAbs
        }
    }

    return $null
}

function Ensure-ServerRuntimeLayout {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [bool]$EnableAutoRepair = $true
    )

    $notes = New-Object System.Collections.Generic.List[string]

    $exeDir = Split-Path -Path $ExecutablePath -Parent
    $rootDir = Split-Path -Path $exeDir -Parent
    $exeName = [System.IO.Path]::GetFileNameWithoutExtension($ExecutablePath)

    $expectedDataDir = Join-Path $exeDir ($exeName + "_Data")
    $expectedMonoDir = Join-Path $exeDir "MonoBleedingEdge"

    if (-not (Test-Path -LiteralPath $expectedDataDir -PathType Container) -and $EnableAutoRepair) {
        $dataSource = Get-FirstExistingDirectory -Candidates @(
            (Join-Path $rootDir ($exeName + "_Data")),
            (Join-Path $rootDir "DigWar_Data")
        ) -ExcludePath $expectedDataDir

        if ($dataSource) {
            Copy-DirectoryReplace -SourcePath $dataSource -DestinationPath $expectedDataDir
            $notes.Add("Auto-repaired missing data folder: $dataSource -> $expectedDataDir")
        }
    }

    if (-not (Test-Path -LiteralPath $expectedMonoDir -PathType Container) -and $EnableAutoRepair) {
        $monoSource = Get-FirstExistingDirectory -Candidates @(
            (Join-Path $rootDir "MonoBleedingEdge")
        ) -ExcludePath $expectedMonoDir

        if ($monoSource) {
            Copy-DirectoryReplace -SourcePath $monoSource -DestinationPath $expectedMonoDir
            $notes.Add("Auto-repaired missing runtime folder: $monoSource -> $expectedMonoDir")
        }
    }

    $missing = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $expectedDataDir -PathType Container)) {
        $missing.Add($expectedDataDir)
    }
    if (-not (Test-Path -LiteralPath $expectedMonoDir -PathType Container)) {
        $missing.Add($expectedMonoDir)
    }

    if ($missing.Count -gt 0) {
        $missingJoined = ($missing -join ", ")
        throw "Server runtime layout is incomplete. Missing: $missingJoined"
    }

    return $notes
}

if ($Port -le 0 -or $Port -gt 65535) {
    throw "Port must be between 1 and 65535."
}

$serverBuildPathAbs = Resolve-AbsolutePath -Path $ServerBuildPath
if (-not (Test-Path -LiteralPath $serverBuildPathAbs -PathType Leaf)) {
    throw "ServerBuildPath not found: $serverBuildPathAbs"
}
$layoutNotes = Ensure-ServerRuntimeLayout -ExecutablePath $serverBuildPathAbs -EnableAutoRepair:(-not $DisableLayoutAutoRepair.IsPresent)

$outputDirAbs = Resolve-AbsolutePath -Path $OutputDir
if (-not (Test-Path -LiteralPath $outputDirAbs)) {
    New-Item -Path $outputDirAbs -ItemType Directory | Out-Null
}

$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd_HHmmss")
$unityLogPath = Join-Path $outputDirAbs ("dedicated_server_" + $timestamp + "_unity.log")
$stdoutPath = Join-Path $outputDirAbs ("dedicated_server_" + $timestamp + "_stdout.log")
$stderrPath = Join-Path $outputDirAbs ("dedicated_server_" + $timestamp + "_stderr.log")
$reportMd = Join-Path $outputDirAbs ("dedicated_server_" + $timestamp + ".md")
$reportJson = [System.IO.Path]::ChangeExtension($reportMd, ".json")

$argList = New-Object System.Collections.Generic.List[string]
$argList.Add("-batchmode")
$argList.Add("-nographics")
$argList.Add("-logFile")
$argList.Add($unityLogPath)
$argList.Add("-dw-mode=server")
$argList.Add("-dw-port=$Port")

if (-not [string]::IsNullOrWhiteSpace($AdditionalArgs)) {
    $extra = $AdditionalArgs.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
    foreach ($token in $extra) {
        $argList.Add($token)
    }
}

$proc = Start-Process -FilePath $serverBuildPathAbs `
    -ArgumentList $argList `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath `
    -WindowStyle Hidden `
    -PassThru

Start-Sleep -Seconds ([Math]::Max(1, $StartupWaitSeconds))

$portOpen = $false
try {
    $probe = Test-NetConnection -ComputerName "127.0.0.1" -Port $Port -WarningAction SilentlyContinue
    $portOpen = [bool]$probe.TcpTestSucceeded
} catch {
    $portOpen = $false
}

$notes = New-Object System.Collections.Generic.List[string]
foreach ($layoutNote in $layoutNotes) {
    $notes.Add($layoutNote)
}
if (-not $portOpen) {
    $notes.Add("Port check failed. Verify server boot log and transport settings.")
}

$result = [ordered]@{
    createdAtUtc   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    status         = if ($portOpen) { "PASS" } else { "CHECK" }
    serverBuildPath = $serverBuildPathAbs
    serverPid      = $proc.Id
    port           = $Port
    portOpen       = $portOpen
    localEndpoint  = "127.0.0.1:$Port"
    unityLogPath   = $unityLogPath
    stdoutPath     = $stdoutPath
    stderrPath     = $stderrPath
    commandLine    = ($argList -join " ")
    notes          = $notes
}

New-MarkdownReport -Path $reportMd -Result $result
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportJson -Encoding UTF8

Write-Host "[Server] Status: $($result.status)"
Write-Host "[Server] PID: $($result.serverPid)"
Write-Host "[Server] Endpoint: $($result.localEndpoint)"
Write-Host "[Server] UnityLog: $unityLogPath"
Write-Host "[Server] Report(MD): $reportMd"
Write-Host "[Server] Report(JSON): $reportJson"
