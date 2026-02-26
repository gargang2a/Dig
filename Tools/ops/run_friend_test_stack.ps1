param(
    [ValidateSet("start", "status", "stop")]
    [string]$Action = "start",

    [string]$ServerBuildPath = ".\Build\DigWarServer\DigWar.exe",
    [int]$ServerPort = 7778,
    [string]$SiteUrl = "https://gargang2a.github.io/Dig/",
    [string]$OutputDir = ".\build\ops",
    [int]$ServerStartupWaitSeconds = 8,
    [int]$TunnelWaitSeconds = 30,

    [switch]$InstallCloudflaredIfMissing,
    [switch]$KillExistingServer,
    [switch]$KillExistingTunnel,
    [switch]$StopAll
)

$ErrorActionPreference = "Stop"

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

function New-MarkdownSummary {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][pscustomobject]$State
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Friend Test Stack Summary")
    $lines.Add("")
    $lines.Add("- CreatedAtUtc: $($State.createdAtUtc)")
    $lines.Add("- UpdatedAtUtc: $($State.updatedAtUtc)")
    $lines.Add("- Mode: $($State.mode)")
    $lines.Add("- SessionId: $($State.sessionId)")
    $lines.Add("- OverallStatus: $($State.overallStatus)")
    $lines.Add("")
    $lines.Add("## Server")
    $lines.Add("")
    $lines.Add("- BuildPath: $($State.server.buildPath)")
    $lines.Add("- PID: $($State.server.pid)")
    $lines.Add("- Endpoint: $($State.server.endpoint)")
    $lines.Add("- Running: $($State.server.running)")
    $lines.Add("- PortOpen: $($State.server.portOpen)")
    $lines.Add("- PortOwnedByServerPid: $($State.server.portOwnedByServerPid)")
    $lines.Add("- ReportPath: $($State.server.reportPath)")
    $lines.Add("")
    $lines.Add("## Tunnel")
    $lines.Add("")
    $lines.Add("- PID: $($State.tunnel.pid)")
    $lines.Add("- Running: $($State.tunnel.running)")
    $lines.Add("- TunnelUrl: $($State.tunnel.tunnelUrl)")
    $lines.Add("- FriendJoinUrl: $($State.tunnel.friendJoinUrl)")
    $lines.Add("- ReportPath: $($State.tunnel.reportPath)")
    $lines.Add("")

    if ($null -ne $State.health) {
        $lines.Add("## Health")
        $lines.Add("")
        $lines.Add("- Status: $($State.health.status)")
        $lines.Add("- BotTargetCount: $($State.health.botTargetCount)")
        $lines.Add("- MaxBotIndexSeen: $($State.health.maxBotIndexSeen)")
        $lines.Add("- SandwormSpawnCount: $($State.health.sandwormSpawnCount)")
        $lines.Add("- ReportPath: $($State.health.reportPath)")
        $lines.Add("")
    }

    if ($State.notes.Count -gt 0) {
        $lines.Add("## Notes")
        $lines.Add("")
        foreach ($note in $State.notes) {
            $lines.Add("- $note")
        }
        $lines.Add("")
    }

    $lines -join "`n" | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Test-ProcessRunning {
    param([int]$ProcessId)

    if ($ProcessId -le 0) {
        return $false
    }

    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    return $null -ne $proc
}

function Invoke-ServerHealthCheck {
    param(
        [Parameter(Mandatory = $true)][string]$CheckScriptPath,
        [Parameter(Mandatory = $true)][string]$ServerReportPath,
        [Parameter(Mandatory = $true)][string]$HealthOutDir
    )

    if (-not (Test-Path -LiteralPath $CheckScriptPath -PathType Leaf)) {
        return [pscustomobject]@{
            status = "CHECK"
            botTargetCount = 0
            maxBotIndexSeen = -1
            sandwormSpawnCount = 0
            reportPath = ""
            notes = @("Health check script not found.")
        }
    }

    & $CheckScriptPath -ServerReportPath $ServerReportPath -OutputDir $HealthOutDir | Out-Null

    $healthReportFile = Get-LatestReportFile -DirectoryPath $HealthOutDir -Pattern "server_runtime_health_*.json"
    if ($null -eq $healthReportFile) {
        return [pscustomobject]@{
            status = "CHECK"
            botTargetCount = 0
            maxBotIndexSeen = -1
            sandwormSpawnCount = 0
            reportPath = ""
            notes = @("Health report file was not generated.")
        }
    }

    $health = Read-JsonFile -Path $healthReportFile.FullName
    return [pscustomobject]@{
        status = [string]$health.status
        botTargetCount = [int]$health.botTargetCount
        maxBotIndexSeen = [int]$health.maxBotIndexSeen
        sandwormSpawnCount = [int]$health.sandwormSpawnCount
        reportPath = $healthReportFile.FullName
        notes = @($health.notes)
    }
}

$repoRoot = Resolve-AbsolutePath -Path "."
$toolRoot = Split-Path -Path $PSCommandPath -Parent
$startServerScript = Resolve-AbsolutePath -Path (Join-Path $toolRoot "..\server\start_dedicated_server.ps1")
$stopServerScript = Resolve-AbsolutePath -Path (Join-Path $toolRoot "..\server\stop_dedicated_server.ps1")
$startTunnelScript = Resolve-AbsolutePath -Path (Join-Path $toolRoot "..\web\start_friend_test_tunnel.ps1")
$stopTunnelScript = Resolve-AbsolutePath -Path (Join-Path $toolRoot "..\web\stop_friend_test_tunnel.ps1")
$checkHealthScript = Resolve-AbsolutePath -Path (Join-Path $toolRoot ".\check_server_runtime_health.ps1")

$outputDirAbs = Resolve-AbsolutePath -Path $OutputDir
$serverOutDir = Join-Path $outputDirAbs "server"
$tunnelOutDir = Join-Path $outputDirAbs "tunnel"
$healthOutDir = Join-Path $outputDirAbs "health"
Ensure-Directory -Path $outputDirAbs
Ensure-Directory -Path $serverOutDir
Ensure-Directory -Path $tunnelOutDir
Ensure-Directory -Path $healthOutDir

$statePath = Join-Path $outputDirAbs "friend_test_stack_state.json"
$summaryPath = Join-Path $outputDirAbs "friend_test_stack_state.md"

switch ($Action.ToLowerInvariant()) {
    "start" {
        $serverBuildPathAbs = Resolve-AbsolutePath -Path $ServerBuildPath
        if (-not (Test-Path -LiteralPath $serverBuildPathAbs -PathType Leaf)) {
            throw "ServerBuildPath not found: $serverBuildPathAbs"
        }

        if ($KillExistingServer) {
            Write-Host "[Stack] Stopping existing dedicated-server processes..."
            & $stopServerScript -StopAllByName
        }

        Write-Host "[Stack] Starting dedicated server..."
        & $startServerScript `
            -ServerBuildPath $serverBuildPathAbs `
            -Port $ServerPort `
            -OutputDir $serverOutDir `
            -StartupWaitSeconds $ServerStartupWaitSeconds

        $serverReportFile = Get-LatestReportFile -DirectoryPath $serverOutDir -Pattern "dedicated_server_*.json"
        if ($null -eq $serverReportFile) {
            throw "Server report file not found in: $serverOutDir"
        }
        $serverReport = Read-JsonFile -Path $serverReportFile.FullName

        Write-Host "[Stack] Starting friend test tunnel..."
        $tunnelArgs = @{
            LocalPort = $ServerPort
            SiteUrl = $SiteUrl
            OutputDir = $tunnelOutDir
            WaitSeconds = $TunnelWaitSeconds
        }
        if ($InstallCloudflaredIfMissing) {
            $tunnelArgs["InstallIfMissing"] = $true
        }
        if ($KillExistingTunnel) {
            $tunnelArgs["KillExisting"] = $true
        }
        & $startTunnelScript @tunnelArgs

        $tunnelReportFile = Get-LatestReportFile -DirectoryPath $tunnelOutDir -Pattern "friend_tunnel_*.json"
        if ($null -eq $tunnelReportFile) {
            throw "Tunnel report file not found in: $tunnelOutDir"
        }
        $tunnelReport = Read-JsonFile -Path $tunnelReportFile.FullName

        $serverRunning = Test-ProcessRunning -ProcessId ([int]$serverReport.serverPid)
        $tunnelRunning = Test-ProcessRunning -ProcessId ([int]$tunnelReport.cloudflaredPid)

        $notes = New-Object System.Collections.Generic.List[string]
        if ($serverReport.status -ne "PASS") {
            $notes.Add("Server status is $($serverReport.status). Check server report/log.")
        }

        # Port owner mismatch means a different process is listening; cleanup newly spawned server pid.
        if (($serverReport.status -eq "CHECK") -and (-not [bool]$serverReport.portOwnedByServerPid) -and $serverRunning) {
            try {
                & $stopServerScript -ProcessId ([int]$serverReport.serverPid) | Out-Null
                $serverRunning = $false
                $notes.Add("Stopped orphan server process pid=$($serverReport.serverPid) because port owner mismatch was detected.")
            } catch {
                $notes.Add("Failed to stop orphan server process pid=$($serverReport.serverPid).")
            }
        }

        if (-not $serverRunning) {
            $notes.Add("Server process is not running.")
        }
        if (-not $tunnelRunning) {
            $notes.Add("Tunnel process is not running.")
        }

        $health = Invoke-ServerHealthCheck `
            -CheckScriptPath $checkHealthScript `
            -ServerReportPath $serverReportFile.FullName `
            -HealthOutDir $healthOutDir
        if ($health.status -ne "PASS") {
            $notes.Add("Health check is $($health.status).")
        }
        foreach ($healthNote in $health.notes) {
            if (-not [string]::IsNullOrWhiteSpace([string]$healthNote)) {
                $notes.Add("Health: $healthNote")
            }
        }

        $overallStatus = if (
            $serverRunning -and
            $tunnelRunning -and
            $serverReport.status -eq "PASS" -and
            $health.status -eq "PASS") { "RUNNING" } else { "CHECK" }
        $createdUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

        $state = [pscustomobject]@{
            createdAtUtc = $createdUtc
            updatedAtUtc = $createdUtc
            mode = "start"
            sessionId = (Get-Date).ToUniversalTime().ToString("yyyyMMdd_HHmmss")
            overallStatus = $overallStatus
            server = [pscustomobject]@{
                buildPath = $serverBuildPathAbs
                pid = [int]$serverReport.serverPid
                endpoint = "127.0.0.1:$ServerPort"
                running = $serverRunning
                portOpen = [bool]$serverReport.portOpen
                portOwnedByServerPid = [bool]$serverReport.portOwnedByServerPid
                reportPath = $serverReportFile.FullName
            }
            tunnel = [pscustomobject]@{
                pid = [int]$tunnelReport.cloudflaredPid
                running = $tunnelRunning
                tunnelUrl = [string]$tunnelReport.tunnelUrl
                friendJoinUrl = [string]$tunnelReport.friendJoinUrl
                reportPath = $tunnelReportFile.FullName
            }
            health = $health
            notes = $notes
        }

        $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statePath -Encoding UTF8
        New-MarkdownSummary -Path $summaryPath -State $state

        Write-Host "[Stack] Status: $($state.overallStatus)"
        Write-Host "[Stack] Friend Join URL: $($state.tunnel.friendJoinUrl)"
        Write-Host "[Stack] State(JSON): $statePath"
        Write-Host "[Stack] State(MD): $summaryPath"
        exit 0
    }

    "status" {
        if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
            throw "State file not found: $statePath. Run with -Action start first."
        }

        $state = Read-JsonFile -Path $statePath
        $serverPid = [int]$state.server.pid
        $tunnelPid = [int]$state.tunnel.pid

        $serverRunning = Test-ProcessRunning -ProcessId $serverPid
        $tunnelRunning = Test-ProcessRunning -ProcessId $tunnelPid

        $portOpen = $false
        $portOwnedByServerPid = $false
        try {
            $listeners = Get-NetTCPConnection -State Listen -LocalPort $ServerPort -ErrorAction SilentlyContinue
            $listenerPids = @($listeners | Select-Object -ExpandProperty OwningProcess -Unique)
            $portOpen = $listenerPids.Count -gt 0
            $portOwnedByServerPid = $listenerPids -contains $serverPid
        } catch {
            $portOpen = $false
            $portOwnedByServerPid = $false
        }

        $notes = New-Object System.Collections.Generic.List[string]
        if (-not $serverRunning) { $notes.Add("Server process is not running.") }
        if (-not $portOpen) { $notes.Add("Server port $ServerPort is not open.") }
        if ($portOpen -and -not $portOwnedByServerPid) { $notes.Add("Server port is owned by another process.") }
        if (-not $tunnelRunning) { $notes.Add("Tunnel process is not running.") }

        $health = Invoke-ServerHealthCheck `
            -CheckScriptPath $checkHealthScript `
            -ServerReportPath ([string]$state.server.reportPath) `
            -HealthOutDir $healthOutDir
        if ($health.status -ne "PASS") { $notes.Add("Health check is $($health.status).") }
        foreach ($healthNote in $health.notes) {
            if (-not [string]::IsNullOrWhiteSpace([string]$healthNote)) {
                $notes.Add("Health: $healthNote")
            }
        }

        $overallStatus = if (
            $serverRunning -and
            $portOpen -and
            $portOwnedByServerPid -and
            $tunnelRunning -and
            $health.status -eq "PASS") { "RUNNING" } else { "CHECK" }

        $state.updatedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        $state.mode = "status"
        $state.overallStatus = $overallStatus
        $state.server.running = $serverRunning
        $state.server.portOpen = $portOpen
        $state.server.portOwnedByServerPid = $portOwnedByServerPid
        $state.tunnel.running = $tunnelRunning
        if ($null -eq $state.PSObject.Properties["health"]) {
            $state | Add-Member -MemberType NoteProperty -Name "health" -Value $health
        } else {
            $state.health = $health
        }
        $state.notes = $notes

        $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statePath -Encoding UTF8
        New-MarkdownSummary -Path $summaryPath -State $state

        Write-Host "[Stack] Status: $overallStatus"
        Write-Host "[Stack] Server: running=$serverRunning, portOpen=$portOpen, ownedByServer=$portOwnedByServerPid"
        Write-Host "[Stack] Tunnel: running=$tunnelRunning"
        Write-Host "[Stack] Health: status=$($health.status), botTarget=$($health.botTargetCount), maxBotIndex=$($health.maxBotIndexSeen), sandwormSpawnCount=$($health.sandwormSpawnCount)"
        Write-Host "[Stack] Friend Join URL: $($state.tunnel.friendJoinUrl)"
        Write-Host "[Stack] State(JSON): $statePath"
        Write-Host "[Stack] State(MD): $summaryPath"
        exit 0
    }

    "stop" {
        if ($StopAll) {
            Write-Host "[Stack] Stopping all dedicated-server and tunnel processes..."
            & $stopTunnelScript -StopAllCloudflared
            & $stopServerScript -StopAllByName
            if (Test-Path -LiteralPath $statePath -PathType Leaf) {
                Remove-Item -LiteralPath $statePath -Force
            }
            if (Test-Path -LiteralPath $summaryPath -PathType Leaf) {
                Remove-Item -LiteralPath $summaryPath -Force
            }
            Write-Host "[Stack] Completed stop-all."
            exit 0
        }

        if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
            throw "State file not found: $statePath. Use -StopAll to force stop by filter."
        }

        $state = Read-JsonFile -Path $statePath
        $tunnelReportPath = [string]$state.tunnel.reportPath
        $serverReportPath = [string]$state.server.reportPath

        if (-not [string]::IsNullOrWhiteSpace($tunnelReportPath) -and (Test-Path -LiteralPath $tunnelReportPath -PathType Leaf)) {
            & $stopTunnelScript -ReportPath $tunnelReportPath
        } elseif ([int]$state.tunnel.pid -gt 0) {
            & $stopTunnelScript -ProcessId ([int]$state.tunnel.pid)
        }

        if (-not [string]::IsNullOrWhiteSpace($serverReportPath) -and (Test-Path -LiteralPath $serverReportPath -PathType Leaf)) {
            & $stopServerScript -ReportPath $serverReportPath
        } elseif ([int]$state.server.pid -gt 0) {
            & $stopServerScript -ProcessId ([int]$state.server.pid)
        }

        Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $summaryPath -Force -ErrorAction SilentlyContinue

        Write-Host "[Stack] Stopped stack by saved state."
        exit 0
    }
}
