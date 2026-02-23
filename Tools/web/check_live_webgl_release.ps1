param(
    [string]$SiteUrl = "https://gargang2a.github.io/Dig/",
    [string]$ExpectedTitleContains = "Unity WebGL Player",
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

function New-CheckRecord {
    param(
        [string]$Name,
        [string]$Url,
        [bool]$Ok,
        [int]$StatusCode,
        [string]$Note = ""
    )

    return [ordered]@{
        name       = $Name
        url        = $Url
        ok         = $Ok
        statusCode = $StatusCode
        note       = $Note
    }
}

function Invoke-UrlProbe {
    param(
        [Parameter(Mandatory = $true)][string]$Url
    )

    try {
        $resp = Invoke-WebRequest -Uri $Url -Method Head -UseBasicParsing -TimeoutSec 20
        return [ordered]@{
            ok         = ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 300)
            statusCode = [int]$resp.StatusCode
            note       = "HEAD"
        }
    } catch {
        $statusCode = 0
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            $statusCode = [int]$_.Exception.Response.StatusCode.value__
        }

        # Some hosts block HEAD. Fallback to a tiny ranged GET.
        try {
            $resp = Invoke-WebRequest -Uri $Url -Method Get -UseBasicParsing -TimeoutSec 30 -Headers @{ Range = "bytes=0-0" }
            return [ordered]@{
                ok         = ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 300)
                statusCode = [int]$resp.StatusCode
                note       = "GET(range)"
            }
        } catch {
            if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
                $statusCode = [int]$_.Exception.Response.StatusCode.value__
            }
            return [ordered]@{
                ok         = $false
                statusCode = $statusCode
                note       = "HEAD/GET failed"
            }
        }
    }
}

function Parse-IndexExpectedFiles {
    param([Parameter(Mandatory = $true)][string]$IndexHtml)

    $result = [ordered]@{
        buildDir  = "Build"
        loader    = $null
        data      = $null
        framework = $null
        wasm      = $null
    }

    $buildDirMatch = [regex]::Match($IndexHtml, 'var\s+buildUrl\s*=\s*"([^"]+)"')
    if ($buildDirMatch.Success -and -not [string]::IsNullOrWhiteSpace($buildDirMatch.Groups[1].Value)) {
        $result.buildDir = $buildDirMatch.Groups[1].Value.Trim().Trim('/')
    }

    $patterns = @{
        loader    = 'loaderUrl\s*=\s*buildUrl\s*\+\s*"/([^"]+)"'
        data      = 'dataUrl:\s*buildUrl\s*\+\s*"/([^"]+)"'
        framework = 'frameworkUrl:\s*buildUrl\s*\+\s*"/([^"]+)"'
        wasm      = 'codeUrl:\s*buildUrl\s*\+\s*"/([^"]+)"'
    }

    foreach ($k in $patterns.Keys) {
        $m = [regex]::Match($IndexHtml, $patterns[$k])
        if ($m.Success) {
            $result[$k] = $m.Groups[1].Value
        }
    }

    return $result
}

function Write-ReportMarkdown {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][hashtable]$Result
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Live WebGL Health Check")
    $lines.Add("")
    $lines.Add("- CheckedAtUtc: $($Result.checkedAtUtc)")
    $lines.Add("- SiteUrl: $($Result.siteUrl)")
    $lines.Add("- Status: $($Result.status)")
    $lines.Add("")
    $lines.Add("## Checks")
    $lines.Add("")
    $lines.Add("| Name | OK | Status | URL | Note |")
    $lines.Add("| :--- | :---: | ---: | :--- | :--- |")

    foreach ($c in $Result.checks) {
        $ok = if ($c.ok) { "Y" } else { "N" }
        $note = if ([string]::IsNullOrWhiteSpace($c.note)) { "-" } else { $c.note }
        $lines.Add("| $($c.name) | $ok | $($c.statusCode) | $($c.url) | $note |")
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

$siteUri = New-Object System.Uri($SiteUrl)
$errors = New-Object System.Collections.Generic.List[string]
$checks = New-Object System.Collections.Generic.List[object]

# 1) Index fetch
$indexStatus = 0
$indexHtml = $null
try {
    $indexResp = Invoke-WebRequest -Uri $siteUri.AbsoluteUri -UseBasicParsing -TimeoutSec 30
    $indexStatus = [int]$indexResp.StatusCode
    $indexHtml = $indexResp.Content
    $checks.Add((New-CheckRecord -Name "index" -Url $siteUri.AbsoluteUri -Ok ($indexStatus -eq 200) -StatusCode $indexStatus -Note "GET"))
} catch {
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
        $indexStatus = [int]$_.Exception.Response.StatusCode.value__
    }
    $checks.Add((New-CheckRecord -Name "index" -Url $siteUri.AbsoluteUri -Ok $false -StatusCode $indexStatus -Note "GET failed"))
    $errors.Add("Index request failed: HTTP $indexStatus")
}

# 2) Title check
if ($indexHtml) {
    $title = [regex]::Match($indexHtml, '<title>(.*?)</title>').Groups[1].Value
    $okTitle = $true
    if (-not [string]::IsNullOrWhiteSpace($ExpectedTitleContains)) {
        $okTitle = $title -like ("*" + $ExpectedTitleContains + "*")
    }
    $checks.Add((New-CheckRecord -Name "title" -Url $siteUri.AbsoluteUri -Ok $okTitle -StatusCode $indexStatus -Note $title))
    if (-not $okTitle) {
        $errors.Add("Title mismatch. Expected contains '$ExpectedTitleContains', actual '$title'")
    }
}

# 3) Build asset checks
if ($indexHtml) {
    $parsed = Parse-IndexExpectedFiles -IndexHtml $indexHtml
    foreach ($k in @("loader", "data", "framework", "wasm")) {
        $file = $parsed[$k]
        if ([string]::IsNullOrWhiteSpace($file)) {
            $checks.Add((New-CheckRecord -Name $k -Url "-" -Ok $false -StatusCode 0 -Note "Missing in index.html"))
            $errors.Add("Missing `$k URL in index.html")
            continue
        }

        $assetRelative = ($parsed.buildDir.Trim().Trim('/') + "/" + $file.TrimStart('/')).Replace("\", "/")
        $assetUri = New-Object System.Uri($siteUri, $assetRelative)
        $probe = Invoke-UrlProbe -Url $assetUri.AbsoluteUri
        $checks.Add((New-CheckRecord -Name $k -Url $assetUri.AbsoluteUri -Ok $probe.ok -StatusCode $probe.statusCode -Note $probe.note))
        if (-not $probe.ok) {
            $errors.Add("Asset check failed: $k ($($probe.statusCode)) -> $($assetUri.AbsoluteUri)")
        }
    }
}

$status = if ($errors.Count -eq 0) { "PASS" } else { "FAIL" }

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $defaultDir = Resolve-AbsolutePath -Path ".\build\github-pages"
    if (-not (Test-Path -LiteralPath $defaultDir)) {
        New-Item -ItemType Directory -Path $defaultDir | Out-Null
    }
    $ReportPath = Join-Path $defaultDir ("live_check_" + (Get-Date).ToUniversalTime().ToString("yyyyMMdd_HHmmss") + ".md")
}

$reportMd = Resolve-AbsolutePath -Path $ReportPath
$reportJson = [System.IO.Path]::ChangeExtension($reportMd, ".json")

$result = [ordered]@{
    checkedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    siteUrl      = $siteUri.AbsoluteUri
    status       = $status
    checks       = $checks
    errors       = $errors
}

Write-ReportMarkdown -Path $reportMd -Result $result
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportJson -Encoding UTF8

Write-Host "[LiveCheck] Status: $status"
Write-Host "[LiveCheck] Report(MD): $reportMd"
Write-Host "[LiveCheck] Report(JSON): $reportJson"

if ($status -ne "PASS") {
    exit 2
}
