param(
    [string]$ReleasePath = ".\build\releases",
    [switch]$Latest,
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"

function Resolve-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Get-RelativePathCompat {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $base = $BasePath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $baseUri = New-Object System.Uri($base)
    $targetUri = New-Object System.Uri($TargetPath)
    $relativeUri = $baseUri.MakeRelativeUri($targetUri)
    return [System.Uri]::UnescapeDataString($relativeUri.ToString()).Replace("\", "/")
}

function Find-WebGlRoot {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    $candidates = Get-ChildItem -LiteralPath $RootPath -Filter "index.html" -File -Recurse |
        Sort-Object FullName

    foreach ($candidate in $candidates) {
        $dir = Split-Path -Path $candidate.FullName -Parent
        $buildDir = Join-Path $dir "Build"
        if (Test-Path -LiteralPath $buildDir) {
            return $dir
        }
    }

    throw "[Validate] Could not find WebGL root (index.html + Build/) under: $RootPath"
}

function Parse-IndexExpectedFiles {
    param([Parameter(Mandatory = $true)][string]$IndexPath)

    $content = Get-Content -LiteralPath $IndexPath -Raw

    $result = [ordered]@{
        loader    = $null
        data      = $null
        framework = $null
        wasm      = $null
    }

    $patterns = @{
        loader    = 'loaderUrl\s*=\s*buildUrl\s*\+\s*"/([^"]+)"'
        data      = 'dataUrl:\s*buildUrl\s*\+\s*"/([^"]+)"'
        framework = 'frameworkUrl:\s*buildUrl\s*\+\s*"/([^"]+)"'
        wasm      = 'codeUrl:\s*buildUrl\s*\+\s*"/([^"]+)"'
    }

    foreach ($k in $patterns.Keys) {
        $m = [regex]::Match($content, $patterns[$k])
        if ($m.Success) {
            $result[$k] = $m.Groups[1].Value
        }
    }

    return $result
}

function Resolve-RequiredFiles {
    param(
        [Parameter(Mandatory = $true)][string]$BuildDir,
        [Parameter(Mandatory = $true)][hashtable]$Parsed
    )

    $resolved = [ordered]@{
        loader    = $null
        data      = $null
        framework = $null
        wasm      = $null
    }

    if ($Parsed.loader) {
        $resolved.loader = Join-Path $BuildDir $Parsed.loader
    } else {
        $resolved.loader = (Get-ChildItem -LiteralPath $BuildDir -File | Where-Object { $_.Name -match '\.loader\.js$' } | Select-Object -First 1).FullName
    }

    if ($Parsed.data) {
        $resolved.data = Join-Path $BuildDir $Parsed.data
    } else {
        $resolved.data = (Get-ChildItem -LiteralPath $BuildDir -File | Where-Object { $_.Name -match '\.data(\.br|\.gz)?$' } | Select-Object -First 1).FullName
    }

    if ($Parsed.framework) {
        $resolved.framework = Join-Path $BuildDir $Parsed.framework
    } else {
        $resolved.framework = (Get-ChildItem -LiteralPath $BuildDir -File | Where-Object { $_.Name -match '\.framework\.js(\.br|\.gz)?$' } | Select-Object -First 1).FullName
    }

    if ($Parsed.wasm) {
        $resolved.wasm = Join-Path $BuildDir $Parsed.wasm
    } else {
        $resolved.wasm = (Get-ChildItem -LiteralPath $BuildDir -File | Where-Object { $_.Name -match '\.wasm(\.br|\.gz)?$' } | Select-Object -First 1).FullName
    }

    return $resolved
}

function Build-FileRecord {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$FilePath
    )

    if (-not (Test-Path -LiteralPath $FilePath)) {
        return [ordered]@{
            path     = $FilePath
            relative = $null
            exists   = $false
            bytes    = 0
            sha256   = $null
        }
    }

    $item = Get-Item -LiteralPath $FilePath
    $hash = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash.ToLowerInvariant()

    return [ordered]@{
        path     = $FilePath
        relative = Get-RelativePathCompat -BasePath $BasePath -TargetPath $FilePath
        exists   = $true
        bytes    = $item.Length
        sha256   = $hash
    }
}

function Write-ValidationMarkdown {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][hashtable]$Result
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# WebGL Release Validation")
    $lines.Add("")
    $lines.Add("- ValidatedAtUtc: $($Result.validatedAtUtc)")
    $lines.Add("- SourceInput: $($Result.sourceInput)")
    $lines.Add("- Mode: $($Result.mode)")
    $lines.Add("- WebGlRoot: $($Result.webGlRoot)")
    $lines.Add("- Status: $($Result.status)")
    $lines.Add("")
    $lines.Add("## Required Files")
    $lines.Add("")
    $lines.Add("| Key | Exists | Bytes | Relative Path | SHA256 |")
    $lines.Add("| :--- | :---: | ---: | :--- | :--- |")

    foreach ($k in @("index", "loader", "data", "framework", "wasm")) {
        $r = $Result.required[$k]
        $existsStr = if ($r.exists) { "Y" } else { "N" }
        $rel = if ($r.relative) { $r.relative } else { "-" }
        $sha = if ($r.sha256) { $r.sha256 } else { "-" }
        $lines.Add("| $k | $existsStr | $($r.bytes) | $rel | $sha |")
    }

    if ($Result.errors.Count -gt 0) {
        $lines.Add("")
        $lines.Add("## Errors")
        $lines.Add("")
        foreach ($e in $Result.errors) {
            $lines.Add("- $e")
        }
    }

    $lines -join "`n" | Set-Content -LiteralPath $Path -Encoding UTF8
}

$releasePathAbs = Resolve-AbsolutePath -Path $ReleasePath

if (-not (Test-Path -LiteralPath $releasePathAbs)) {
    throw "[Validate] ReleasePath not found: $releasePathAbs"
}

$tempDir = $null
$sourceInput = $releasePathAbs
$mode = "folder"
$scanRoot = $releasePathAbs

$item = Get-Item -LiteralPath $releasePathAbs

if ($item.PSIsContainer) {
    $zipCandidates = Get-ChildItem -LiteralPath $releasePathAbs -File -Filter "*.zip" | Sort-Object LastWriteTime -Descending
    if ($zipCandidates.Count -gt 0 -and $Latest) {
        $item = $zipCandidates[0]
        $sourceInput = $item.FullName
    }
}

if (-not $item.PSIsContainer -and $item.Extension -ieq ".zip") {
    $mode = "zip"
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("digwar_webgl_validate_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    Expand-Archive -LiteralPath $item.FullName -DestinationPath $tempDir -Force
    $scanRoot = $tempDir
}

$webGlRoot = Find-WebGlRoot -RootPath $scanRoot
$indexPath = Join-Path $webGlRoot "index.html"
$buildDir = Join-Path $webGlRoot "Build"

$parsed = Parse-IndexExpectedFiles -IndexPath $indexPath
$requiredResolved = Resolve-RequiredFiles -BuildDir $buildDir -Parsed $parsed

$required = [ordered]@{
    index     = Build-FileRecord -BasePath $webGlRoot -FilePath $indexPath
    loader    = Build-FileRecord -BasePath $webGlRoot -FilePath $requiredResolved.loader
    data      = Build-FileRecord -BasePath $webGlRoot -FilePath $requiredResolved.data
    framework = Build-FileRecord -BasePath $webGlRoot -FilePath $requiredResolved.framework
    wasm      = Build-FileRecord -BasePath $webGlRoot -FilePath $requiredResolved.wasm
}

$errors = New-Object System.Collections.Generic.List[string]
foreach ($k in $required.Keys) {
    $r = $required[$k]
    if (-not $r.exists) {
        $errors.Add("Missing required file: $k")
        continue
    }
    if ($r.bytes -le 0) {
        $errors.Add("Empty required file: $k ($($r.relative))")
    }
}

$status = if ($errors.Count -eq 0) { "PASS" } else { "FAIL" }

$result = [ordered]@{
    validatedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    sourceInput    = $sourceInput
    mode           = $mode
    webGlRoot      = $webGlRoot
    status         = $status
    required       = $required
    errors         = $errors
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    if ($mode -eq "zip") {
        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($sourceInput)
        $reportDir = Split-Path -Path $sourceInput -Parent
        $ReportPath = Join-Path $reportDir ($baseName + "_validation.md")
    } else {
        $ReportPath = Join-Path $webGlRoot ("webgl_validation_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".md")
    }
}

$reportPathAbs = Resolve-AbsolutePath -Path $ReportPath
$reportJsonAbs = [System.IO.Path]::ChangeExtension($reportPathAbs, ".json")

Write-ValidationMarkdown -Path $reportPathAbs -Result $result
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportJsonAbs -Encoding UTF8

if ($tempDir -and (Test-Path -LiteralPath $tempDir)) {
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}

Write-Host "[Validate] Status: $status"
Write-Host "[Validate] Report(MD): $reportPathAbs"
Write-Host "[Validate] Report(JSON): $reportJsonAbs"

if ($status -ne "PASS") {
    exit 2
}
