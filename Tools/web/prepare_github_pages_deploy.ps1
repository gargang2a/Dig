param(
    [string]$ReleasePath = ".\build\releases",
    [switch]$Latest,
    [string]$OutputDir = ".\build\github-pages",
    [string]$RemoteName = "origin",
    [string]$SiteSubPath = "",
    [switch]$SkipValidation
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

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

function Find-WebGlRoot {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    $candidates = Get-ChildItem -LiteralPath $RootPath -Filter "index.html" -File -Recurse |
        Sort-Object FullName

    foreach ($candidate in $candidates) {
        $dir = Split-Path -Path $candidate.FullName -Parent
        if (Test-Path -LiteralPath (Join-Path $dir "Build")) {
            return $dir
        }
    }

    throw "[Prepare] Could not find WebGL root (index.html + Build/) under: $RootPath"
}

function Parse-ReleaseTag {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath
    )

    $name = [System.IO.Path]::GetFileNameWithoutExtension($SourcePath)
    $m = [regex]::Match($name, '^[^_]+_WebGL_(.+)$')
    if ($m.Success -and -not [string]::IsNullOrWhiteSpace($m.Groups[1].Value)) {
        return $m.Groups[1].Value
    }

    return (Get-Date).ToUniversalTime().ToString("yyyyMMdd_HHmmss")
}

function Convert-RemoteToPagesUrl {
    param(
        [Parameter(Mandatory = $true)][string]$RemoteUrl,
        [string]$SiteSubPath = ""
    )

    $owner = $null
    $repo = $null

    $https = [regex]::Match($RemoteUrl, '^https://github\.com/([^/]+)/([^/]+?)(?:\.git)?$')
    if ($https.Success) {
        $owner = $https.Groups[1].Value
        $repo = $https.Groups[2].Value
    }

    if (-not $owner) {
        $ssh = [regex]::Match($RemoteUrl, '^git@github\.com:([^/]+)/([^/]+?)(?:\.git)?$')
        if ($ssh.Success) {
            $owner = $ssh.Groups[1].Value
            $repo = $ssh.Groups[2].Value
        }
    }

    if (-not $owner) {
        return $null
    }

    $path = "/$repo/"
    if (-not [string]::IsNullOrWhiteSpace($SiteSubPath)) {
        $normalized = $SiteSubPath.Trim().Trim('/')
        if ($normalized.Length -gt 0) {
            $path = "/$repo/$normalized/"
        }
    }

    return "https://$owner.github.io$path"
}

function Normalize-SubPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    return $Path.Trim().Trim('/')
}

$releasePathAbs = Resolve-AbsolutePath -Path $ReleasePath
if (-not (Test-Path -LiteralPath $releasePathAbs)) {
    throw "[Prepare] ReleasePath not found: $releasePathAbs"
}

$outputDirAbs = Resolve-AbsolutePath -Path $OutputDir
Ensure-Directory -Path $outputDirAbs

$sourceMode = "folder"
$sourceInput = $releasePathAbs
$sourceItem = Get-Item -LiteralPath $releasePathAbs

if ($sourceItem.PSIsContainer) {
    $zipCandidates = Get-ChildItem -LiteralPath $releasePathAbs -Filter "*.zip" -File | Sort-Object LastWriteTime -Descending
    if ($Latest -or $zipCandidates.Count -gt 0) {
        if ($zipCandidates.Count -gt 0) {
            $sourceItem = $zipCandidates[0]
            $sourceInput = $sourceItem.FullName
        }
    }
}

$tempExtractDir = $null
$scanRoot = $sourceInput

if (-not $sourceItem.PSIsContainer -and $sourceItem.Extension -ieq ".zip") {
    $sourceMode = "zip"
    $tempExtractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("digwar_webgl_pages_" + [Guid]::NewGuid().ToString("N"))
    Ensure-Directory -Path $tempExtractDir
    Expand-Archive -LiteralPath $sourceItem.FullName -DestinationPath $tempExtractDir -Force
    $scanRoot = $tempExtractDir
}

$webGlRoot = Find-WebGlRoot -RootPath $scanRoot
$releaseTag = Parse-ReleaseTag -SourcePath $sourceInput

if (-not $SkipValidation) {
    $validationScript = Join-Path (Resolve-AbsolutePath -Path ".\Tools\web") "validate_webgl_release.ps1"
    if (-not (Test-Path -LiteralPath $validationScript)) {
        throw "[Prepare] Missing dependency script: $validationScript"
    }

    $validationReport = Join-Path $outputDirAbs ("validation_" + $releaseTag + ".md")
    & powershell -ExecutionPolicy Bypass -File $validationScript -ReleasePath $sourceInput -ReportPath $validationReport
    if ($LASTEXITCODE -ne 0) {
        throw "[Prepare] Validation failed. Check report: $validationReport"
    }
}

$normalizedSubPath = Normalize-SubPath -Path $SiteSubPath

$stageRoot = Join-Path $outputDirAbs "staging"
$stageCurrent = Join-Path $stageRoot "current"
$stageVersions = Join-Path $stageRoot "versions"
$stageVersion = Join-Path $stageVersions $releaseTag

if (Test-Path -LiteralPath $stageCurrent) {
    Remove-Item -LiteralPath $stageCurrent -Recurse -Force
}

if (Test-Path -LiteralPath $stageVersion) {
    Remove-Item -LiteralPath $stageVersion -Recurse -Force
}

Ensure-Directory -Path $stageCurrent
Ensure-Directory -Path $stageVersion

Copy-Item -Path (Join-Path $webGlRoot "*") -Destination $stageCurrent -Recurse -Force
Copy-Item -Path (Join-Path $webGlRoot "*") -Destination $stageVersion -Recurse -Force

New-Item -Path (Join-Path $stageCurrent ".nojekyll") -ItemType File -Force | Out-Null
New-Item -Path (Join-Path $stageVersion ".nojekyll") -ItemType File -Force | Out-Null

$remoteUrl = $null
try {
    $remoteUrl = (git remote get-url $RemoteName).Trim()
} catch {
    $remoteUrl = $null
}

$expectedUrl = $null
if ($remoteUrl) {
    $expectedUrl = Convert-RemoteToPagesUrl -RemoteUrl $remoteUrl -SiteSubPath $normalizedSubPath
}

$pagesTargetPath = if ([string]::IsNullOrWhiteSpace($normalizedSubPath)) { "." } else { $normalizedSubPath }
$deployGuidePath = Join-Path $outputDirAbs ("deploy_github_pages_" + $releaseTag + ".md")
$deployJsonPath = Join-Path $outputDirAbs ("deploy_github_pages_" + $releaseTag + ".json")

$guideLines = New-Object System.Collections.Generic.List[string]
$guideLines.Add("# GitHub Pages Deploy Guide")
$guideLines.Add("")
$guideLines.Add("- GeneratedAtUtc: " + (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))
$guideLines.Add("- RemoteName: $RemoteName")
$guideLines.Add("- RemoteUrl: " + ($(if ($remoteUrl) { $remoteUrl } else { "N/A" })))
$guideLines.Add("- ReleaseTag: $releaseTag")
$guideLines.Add("- SourceInput: $sourceInput")
$guideLines.Add("- StageCurrent: $stageCurrent")
$guideLines.Add("- StageVersion: $stageVersion")
$guideLines.Add("- PagesTargetPath: $pagesTargetPath")
$guideLines.Add("- ExpectedPagesUrl: " + ($(if ($expectedUrl) { $expectedUrl } else { "N/A (non-GitHub remote or parse failed)" })))
$guideLines.Add("")
$guideLines.Add("## Deploy Commands (Manual)")
$guideLines.Add("")
$guideLines.Add('```powershell')
$repoRoot = Resolve-AbsolutePath -Path "."
$worktreeRoot = ".\build\gh-pages-worktree"
$worktreeTarget = $worktreeRoot
if ($pagesTargetPath -ne ".") {
    $worktreeTarget = Join-Path $worktreeRoot $pagesTargetPath
}

$guideLines.Add(('Set-Location "{0}"' -f $repoRoot))
$guideLines.Add("git fetch $RemoteName")
$guideLines.Add("git worktree add .\\build\\gh-pages-worktree $RemoteName/gh-pages")
$guideLines.Add(('robocopy "{0}" "{1}" /MIR' -f $stageCurrent, $worktreeTarget))
$guideLines.Add("New-Item -ItemType File -Path .\\build\\gh-pages-worktree\\.nojekyll -Force | Out-Null")
$guideLines.Add("Set-Location .\\build\\gh-pages-worktree")
$guideLines.Add("git add .")
$guideLines.Add(('git commit -m "Deploy WebGL release {0}"' -f $releaseTag))
$guideLines.Add("git push $RemoteName gh-pages")
$guideLines.Add('```')

$guideLines -join "`n" | Set-Content -LiteralPath $deployGuidePath -Encoding UTF8

$deployMeta = [ordered]@{
    generatedAtUtc  = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    remoteName      = $RemoteName
    remoteUrl       = $remoteUrl
    releaseTag      = $releaseTag
    sourceInput     = $sourceInput
    sourceMode      = $sourceMode
    webGlRoot       = $webGlRoot
    stageCurrent    = $stageCurrent
    stageVersion    = $stageVersion
    pagesTargetPath = $pagesTargetPath
    expectedPagesUrl = $expectedUrl
    deployGuidePath = $deployGuidePath
}

$deployMeta | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $deployJsonPath -Encoding UTF8

if ($tempExtractDir -and (Test-Path -LiteralPath $tempExtractDir)) {
    Remove-Item -LiteralPath $tempExtractDir -Recurse -Force
}

Write-Host "[Prepare] GitHub Pages staging ready."
Write-Host "[Prepare] Stage(current): $stageCurrent"
Write-Host "[Prepare] Stage(version): $stageVersion"
Write-Host "[Prepare] Deploy guide: $deployGuidePath"
Write-Host "[Prepare] Deploy metadata: $deployJsonPath"
if ($expectedUrl) {
    Write-Host "[Prepare] Expected URL: $expectedUrl"
}
