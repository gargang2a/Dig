param(
    [string]$BuildRoot = ".\build\WebGL",
    [string]$OutputDir = ".\build\releases",
    [string]$ProductName = "DigWar",
    [string]$ReleaseTag = "",
    [switch]$Force
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

function Require-SingleMatch {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $items = Get-ChildItem -LiteralPath $Directory -File | Where-Object { $_.Name -match $Pattern }
    if (-not $items -or $items.Count -eq 0) {
        throw "[Package] Missing required file: $Label (pattern: $Pattern)"
    }

    return $items | Select-Object -First 1
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

function Write-ManifestJson {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][hashtable]$Metadata
    )

    $Metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
}

function Write-ManifestMarkdown {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][hashtable]$Metadata
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# WebGL Release Manifest")
    $lines.Add("")
    $lines.Add("- GeneratedAtUtc: $($Metadata.generatedAtUtc)")
    $lines.Add("- ProductName: $($Metadata.productName)")
    $lines.Add("- ReleaseTag: $($Metadata.releaseTag)")
    $lines.Add("- SourceBuildRoot: $($Metadata.sourceBuildRoot)")
    $lines.Add("- PackageZip: $($Metadata.packageZip)")
    $lines.Add("- TotalFiles: $($Metadata.totalFiles)")
    $lines.Add("- TotalBytes: $($Metadata.totalBytes)")
    $lines.Add("")
    $lines.Add("## Required Entries")
    $lines.Add("")
    $lines.Add("| Key | Value |")
    $lines.Add("| :--- | :--- |")

    foreach ($k in @("indexHtml", "loaderJs", "dataFile", "frameworkFile", "wasmFile")) {
        $lines.Add("| $k | $($Metadata.required.$k) |")
    }

    $lines.Add("")
    $lines.Add("## File Inventory")
    $lines.Add("")
    $lines.Add("| Path | Bytes | SHA256 |")
    $lines.Add("| :--- | ---: | :--- |")

    foreach ($entry in $Metadata.files) {
        $lines.Add("| $($entry.path) | $($entry.bytes) | $($entry.sha256) |")
    }

    $lines -join "`n" | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
}

if ([string]::IsNullOrWhiteSpace($ReleaseTag)) {
    $ReleaseTag = (Get-Date).ToUniversalTime().ToString("yyyyMMdd_HHmmss")
}

$buildRootAbs = Resolve-AbsolutePath -Path $BuildRoot
$outputDirAbs = Resolve-AbsolutePath -Path $OutputDir

if (-not (Test-Path -LiteralPath $buildRootAbs)) {
    throw "[Package] Build root not found: $buildRootAbs"
}

$indexHtmlPath = Join-Path $buildRootAbs "index.html"
if (-not (Test-Path -LiteralPath $indexHtmlPath)) {
    throw "[Package] Missing required file: index.html"
}

$buildSubDir = Join-Path $buildRootAbs "Build"
if (-not (Test-Path -LiteralPath $buildSubDir)) {
    throw "[Package] Missing required directory: Build"
}

$loader = Require-SingleMatch -Directory $buildSubDir -Pattern "\.loader\.js$" -Label "loader.js"
$data = Require-SingleMatch -Directory $buildSubDir -Pattern "\.data(\.br|\.gz)?$" -Label "data(.br|.gz)"
$framework = Require-SingleMatch -Directory $buildSubDir -Pattern "\.framework\.js(\.br|\.gz)?$" -Label "framework.js(.br|.gz)"
$wasm = Require-SingleMatch -Directory $buildSubDir -Pattern "\.wasm(\.br|\.gz)?$" -Label "wasm(.br|.gz)"

Ensure-Directory -Path $outputDirAbs

$baseName = "{0}_WebGL_{1}" -f $ProductName, $ReleaseTag
$zipPath = Join-Path $outputDirAbs ($baseName + ".zip")
$manifestJsonPath = Join-Path $outputDirAbs ($baseName + "_manifest.json")
$manifestMdPath = Join-Path $outputDirAbs ($baseName + "_manifest.md")

if ((Test-Path -LiteralPath $zipPath) -and (-not $Force)) {
    throw "[Package] Output zip already exists. Use -Force to overwrite: $zipPath"
}

$tempRoot = Join-Path $outputDirAbs ("_tmp_" + $baseName)
$packageFolderName = "{0}_WebGL" -f $ProductName
$packageRoot = Join-Path $tempRoot $packageFolderName

if (Test-Path -LiteralPath $tempRoot) {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}

Ensure-Directory -Path $packageRoot
Copy-Item -Path (Join-Path $buildRootAbs "*") -Destination $packageRoot -Recurse -Force

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal

$inventory = New-Object System.Collections.Generic.List[object]
$packageFiles = Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Sort-Object FullName
$totalBytes = 0L

if ($packageFiles.Count -eq 0) {
    throw "[Package] Package folder is empty after copy. Check BuildRoot path and copy permissions."
}

foreach ($file in $packageFiles) {
    $relative = Get-RelativePathCompat -BasePath $packageRoot -TargetPath $file.FullName
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $totalBytes += $file.Length

    $inventory.Add([ordered]@{
            path   = $relative
            bytes  = $file.Length
            sha256 = $hash
        })
}

$metadata = [ordered]@{
    generatedAtUtc  = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    productName     = $ProductName
    releaseTag      = $ReleaseTag
    sourceBuildRoot = $buildRootAbs
    packageZip      = $zipPath
    totalFiles      = $packageFiles.Count
    totalBytes      = $totalBytes
    required        = [ordered]@{
        indexHtml    = "index.html"
        loaderJs     = ("Build/" + $loader.Name)
        dataFile     = ("Build/" + $data.Name)
        frameworkFile = ("Build/" + $framework.Name)
        wasmFile     = ("Build/" + $wasm.Name)
    }
    files           = $inventory
}

Write-ManifestJson -ManifestPath $manifestJsonPath -Metadata $metadata
Write-ManifestMarkdown -ManifestPath $manifestMdPath -Metadata $metadata

if (Test-Path -LiteralPath $tempRoot) {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}

Write-Host "[Package] WebGL release packaged successfully."
Write-Host "[Package] Zip: $zipPath"
Write-Host "[Package] Manifest(JSON): $manifestJsonPath"
Write-Host "[Package] Manifest(MD): $manifestMdPath"
Write-Host "[Package] Required files: OK (loader/data/framework/wasm)"
