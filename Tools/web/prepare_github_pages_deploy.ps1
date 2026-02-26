param(
    [string]$ReleasePath = ".\build\releases",
    [switch]$Latest,
    [string]$OutputDir = ".\build\github-pages",
    [string]$RemoteName = "origin",
    [string]$SiteSubPath = "",
    [string]$CustomDomain = "",
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

function Normalize-CustomDomain {
    param([string]$Domain)

    if ([string]::IsNullOrWhiteSpace($Domain)) {
        return ""
    }

    return $Domain.Trim().Trim('/').ToLowerInvariant()
}

function Enable-ImmersiveWebGlIndex {
    param([Parameter(Mandatory = $true)][string]$IndexPath)

    if (-not (Test-Path -LiteralPath $IndexPath)) {
        return $false
    }

    $content = Get-Content -LiteralPath $IndexPath -Raw
    $updated = $content

    if ($updated -notmatch "digwar-inline-hide-loading") {
        $inlineHideBlock = @"
    <style id="digwar-inline-hide-loading">
      html, body {
        width: 100%;
        height: 100%;
        margin: 0 !important;
        padding: 0 !important;
        overflow: hidden !important;
        background: #111;
      }
      #unity-container.unity-desktop {
        position: fixed !important;
        left: 0 !important;
        top: 0 !important;
        transform: none !important;
        width: 100vw !important;
        height: 100vh !important;
      }
      #unity-canvas {
        display: block !important;
        width: 100vw !important;
        height: 100vh !important;
      }
      #unity-loading-bar,
      #unity-logo,
      #unity-progress-bar-empty,
      #unity-progress-bar-full,
      #unity-footer,
      #unity-webgl-logo,
      #unity-build-title,
      #unity-fullscreen-button {
        display: none !important;
        visibility: hidden !important;
        opacity: 0 !important;
      }
    </style>
"@
        $updated = $updated -replace '</head>', ($inlineHideBlock + "`r`n  </head>")
    }

    if ($updated -notmatch 'var footer = document.querySelector\("#unity-footer"\);') {
        $updated = $updated -replace 'var warningBanner = document\.querySelector\("#unity-warning"\);',
            "var warningBanner = document.querySelector(""#unity-warning"");`r`n      var footer = document.querySelector(""#unity-footer"");"
    }

    $immersiveDesktopBlock = @"
      } else {
        // Desktop style: fill the browser viewport for immersive play.
        container.className = "unity-desktop";
      }

      // digwar-immersive-mode
      function applyImmersiveCanvasSize() {
        container.style.position = "fixed";
        container.style.left = "0";
        container.style.top = "0";
        container.style.transform = "none";
        container.style.width = "100vw";
        container.style.height = "100vh";
        canvas.style.display = "block";
        canvas.style.width = "100vw";
        canvas.style.height = "100vh";
      }

      window.addEventListener("resize", applyImmersiveCanvasSize);
      applyImmersiveCanvasSize();
"@

    $desktopWindowedBlock = @"
      } else {
        // Desktop style: Render the game canvas in a window that can be maximized to fullscreen:

        canvas.style.width = "960px";
        canvas.style.height = "600px";
      }
"@

    if ($updated.Contains($desktopWindowedBlock)) {
        $updated = $updated.Replace($desktopWindowedBlock, $immersiveDesktopBlock)
    }

    $setFullscreenRegex = 'fullscreenButton\.onclick\s*=\s*\(\)\s*=>\s*\{\s*unityInstance\.SetFullscreen\(1\);\s*\};'
    $hideFullscreenButton = @"
                // digwar-no-browser-fullscreen
                if (fullscreenButton) {
                  fullscreenButton.style.display = "none";
                }
"@
    $updated = [regex]::Replace($updated, $setFullscreenRegex, $hideFullscreenButton, 1)
    $updated = $updated.Replace("unityInstance.SetFullscreen(1);", "// digwar-no-browser-fullscreen")

    if ($updated -notmatch "digwar-no-browser-fullscreen") {
        $loadingHideRegex = 'loadingBar\.style\.display\s*=\s*"none";'
        $loadingHideReplacement = @"
                loadingBar.style.display = "none";
                // digwar-no-browser-fullscreen
                if (fullscreenButton) {
                  fullscreenButton.style.display = "none";
                }
"@
        $updated = [regex]::Replace($updated, $loadingHideRegex, $loadingHideReplacement, 1)
    }

    if ($updated -notmatch "digwar-low-spec-mode") {
        $configAnchor = '      // By default, Unity keeps WebGL canvas render target size matched with'
        if ($updated.Contains($configAnchor)) {
            $lowSpecBlock = @"
      // digwar-low-spec-mode
      var queryParams = new URLSearchParams(window.location.search);
      var lowSpecMode = queryParams.get("low") === "1";
      if (lowSpecMode) {
        config.devicePixelRatio = 1;
        config.webglContextAttributes = Object.assign({}, config.webglContextAttributes || {}, {
          antialias: false,
          alpha: false,
          depth: true,
          stencil: false,
          preserveDrawingBuffer: false,
          powerPreference: "low-power"
        });
      }

"@
            $updated = $updated.Replace($configAnchor, $lowSpecBlock + $configAnchor)
        }
    }

    if ($updated -notmatch "digwar-load-error-fallback") {
        $catchPattern = '\}\)\.catch\(\(message\)\s*=>\s*\{\s*alert\(message\);\s*\}\);'
        $catchReplacement = @"
              }).catch((message) => {
                // digwar-load-error-fallback
                var reason = (typeof message === "string") ? message : JSON.stringify(message);
                console.error("[DigWar] Unity load failed:", reason);
                unityShowBanner("WebGL graphics initialization failed. Enable hardware acceleration and retry with ?low=1", "error");
              });
"@
        $updated = [regex]::Replace($updated, $catchPattern, $catchReplacement, 1)
    }

    if ($updated -notmatch "digwar-clean-ui") {
        $loadingPattern = 'loadingBar\.style\.display\s*=\s*"block";'
        $loadingReplacement = @"
      // digwar-clean-ui
      if (loadingBar) {
        loadingBar.style.display = "none";
      }
"@
        $updated = [regex]::Replace($updated, $loadingPattern, $loadingReplacement, 1)
    }

    if ($updated -ne $content) {
        Set-Content -LiteralPath $IndexPath -Value $updated -Encoding UTF8
        return $true
    }

    return $false
}

function Enable-CleanFullscreenStyle {
    param([Parameter(Mandatory = $true)][string]$StylePath)

    if (-not (Test-Path -LiteralPath $StylePath)) {
        return $false
    }

    $content = Get-Content -LiteralPath $StylePath -Raw

    $legacyMarker = "/* digwar-clean-fullscreen */"
    $legacyMarkerIndex = $content.IndexOf($legacyMarker)
    if ($legacyMarkerIndex -ge 0) {
        $content = $content.Substring(0, $legacyMarkerIndex).TrimEnd()
        $content += "`r`n"
    }

    if ($content -match "digwar-clean-ui") {
        Set-Content -LiteralPath $StylePath -Value $content -Encoding UTF8
        return $false
    }

$cleanBlock = @"

/* digwar-clean-ui */
html, body {
  width: 100%;
  height: 100%;
  margin: 0 !important;
  padding: 0 !important;
  overflow: hidden;
  background: #111;
}

#unity-container.unity-desktop,
#unity-canvas {
  width: 100vw !important;
  height: 100vh !important;
}

#unity-container.unity-desktop {
  position: fixed !important;
  left: 0 !important;
  top: 0 !important;
  transform: none !important;
}

#unity-canvas {
  display: block !important;
}

#unity-loading-bar,
#unity-logo,
#unity-progress-bar-empty,
#unity-progress-bar-full,
#unity-footer,
#unity-webgl-logo,
#unity-build-title,
#unity-fullscreen-button {
  display: none !important;
  visibility: hidden !important;
  opacity: 0 !important;
}

#unity-warning {
  position: fixed !important;
  left: 50% !important;
  top: 20px !important;
  transform: translateX(-50%) !important;
  z-index: 9999 !important;
  max-width: min(92vw, 760px) !important;
  border-radius: 8px !important;
}
"@

    Set-Content -LiteralPath $StylePath -Value ($content + $cleanBlock) -Encoding UTF8
    return $true
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
$normalizedCustomDomain = Normalize-CustomDomain -Domain $CustomDomain

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

$immersivePatchedCurrent = Enable-ImmersiveWebGlIndex -IndexPath (Join-Path $stageCurrent "index.html")
$immersivePatchedVersion = Enable-ImmersiveWebGlIndex -IndexPath (Join-Path $stageVersion "index.html")
$cleanCssPatchedCurrent = Enable-CleanFullscreenStyle -StylePath (Join-Path $stageCurrent "TemplateData\style.css")
$cleanCssPatchedVersion = Enable-CleanFullscreenStyle -StylePath (Join-Path $stageVersion "TemplateData\style.css")

New-Item -Path (Join-Path $stageCurrent ".nojekyll") -ItemType File -Force | Out-Null
New-Item -Path (Join-Path $stageVersion ".nojekyll") -ItemType File -Force | Out-Null

if (-not [string]::IsNullOrWhiteSpace($normalizedCustomDomain)) {
    Set-Content -LiteralPath (Join-Path $stageCurrent "CNAME") -Value $normalizedCustomDomain -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $stageVersion "CNAME") -Value $normalizedCustomDomain -Encoding ASCII
}

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
$guideLines.Add("- CustomDomain: " + ($(if ($normalizedCustomDomain) { $normalizedCustomDomain } else { "N/A" })))
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
if (-not [string]::IsNullOrWhiteSpace($normalizedCustomDomain)) {
    $guideLines.Add(('Set-Content -Path .\\build\\gh-pages-worktree\\CNAME -Value "{0}" -Encoding ASCII' -f $normalizedCustomDomain))
}
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
    immersiveIndexPatchedCurrent = $immersivePatchedCurrent
    immersiveIndexPatchedVersion = $immersivePatchedVersion
    cleanCssPatchedCurrent = $cleanCssPatchedCurrent
    cleanCssPatchedVersion = $cleanCssPatchedVersion
    pagesTargetPath = $pagesTargetPath
    customDomain    = $normalizedCustomDomain
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
