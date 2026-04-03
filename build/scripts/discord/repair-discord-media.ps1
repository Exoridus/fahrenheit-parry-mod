param(
    [string]$Root = ".workspace/discord",
    [int]$TimeoutSec = 30,
    [int]$ThrottleLimit = 12,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-AbsolutePath {
    param([Parameter(Mandatory)][string]$Path)
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Ensure-Directory {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

function Get-TextPrefix {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$MaxBytes = 8192
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -eq 0) {
        return ""
    }

    $count = [Math]::Min($bytes.Length, $MaxBytes)
    return [System.Text.Encoding]::UTF8.GetString($bytes, 0, $count)
}

function Get-DetectedExtension {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 4) {
        return $null
    }

    if ($bytes.Length -ge 8 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47) { return ".png" }
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xD8 -and $bytes[2] -eq 0xFF) { return ".jpg" }
    if ($bytes.Length -ge 4 -and $bytes[0] -eq 0x47 -and $bytes[1] -eq 0x49 -and $bytes[2] -eq 0x46 -and $bytes[3] -eq 0x38) { return ".gif" }
    if ($bytes.Length -ge 12 -and $bytes[0] -eq 0x52 -and $bytes[1] -eq 0x49 -and $bytes[2] -eq 0x46 -and $bytes[3] -eq 0x46 -and
        $bytes[8] -eq 0x57 -and $bytes[9] -eq 0x45 -and $bytes[10] -eq 0x42 -and $bytes[11] -eq 0x50) { return ".webp" }
    if ($bytes.Length -ge 4 -and $bytes[0] -eq 0x25 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x44 -and $bytes[3] -eq 0x46) { return ".pdf" }
    if ($bytes.Length -ge 12 -and $bytes[4] -eq 0x66 -and $bytes[5] -eq 0x74 -and $bytes[6] -eq 0x79 -and $bytes[7] -eq 0x70) { return ".mp4" }
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0x49 -and $bytes[1] -eq 0x44 -and $bytes[2] -eq 0x33) { return ".mp3" }
    if ($bytes.Length -ge 12 -and $bytes[0] -eq 0x52 -and $bytes[1] -eq 0x49 -and $bytes[2] -eq 0x46 -and $bytes[3] -eq 0x46 -and
        $bytes[8] -eq 0x57 -and $bytes[9] -eq 0x41 -and $bytes[10] -eq 0x56 -and $bytes[11] -eq 0x45) { return ".wav" }
    if ($bytes.Length -ge 4 -and $bytes[0] -eq 0x4F -and $bytes[1] -eq 0x67 -and $bytes[2] -eq 0x67 -and $bytes[3] -eq 0x53) { return ".ogg" }

    $text = [System.Text.Encoding]::UTF8.GetString($bytes, 0, [Math]::Min($bytes.Length, 1024))
    if ($text.IndexOf("<!DOCTYPE html", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $text.IndexOf("<html", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { return ".html" }
    if ($text.TrimStart().StartsWith("{") -or $text.TrimStart().StartsWith("[")) { return ".json" }
    if ($text.IndexOf("<?xml", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $text.IndexOf("<svg", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { return ".svg" }

    return $null
}

function Convert-DiscordProxyUrl {
    param([Parameter(Mandatory)][string]$Url)

    if ($Url -notmatch '^https://images-ext-1\.discordapp\.net/external/[^/]+/(?:(?<query>%3F[^/]+)/)?(?<scheme>https|http)/(?<rest>.+)$') {
        return $Url
    }

    $scheme = $Matches["scheme"]
    $rest = $Matches["rest"]
    $querySegment = $Matches["query"]

    $baseUrl = "{0}://{1}" -f $scheme, $rest
    $decodedUrl = [System.Uri]::UnescapeDataString($baseUrl)

    if ([string]::IsNullOrWhiteSpace($querySegment)) {
        return $decodedUrl
    }

    $decodedQuery = [System.Uri]::UnescapeDataString($querySegment)
    if (-not $decodedQuery.StartsWith("?")) {
        $decodedQuery = "?" + $decodedQuery.TrimStart("?")
    }

    return $decodedUrl + $decodedQuery
}

function Get-InvalidResourceRetryUrl {
    param([Parameter(Mandatory)][string]$Path)

    try {
        $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        return $null
    }

    $messageProperty = $json.PSObject.Properties["message"]
    if ($null -eq $messageProperty) {
        return $null
    }

    $message = $messageProperty.Value
    if ($message -isnot [string]) {
        return $null
    }

    if ($message -notmatch '^Invalid resource "(.+)"$') {
        return $null
    }

    return Convert-DiscordProxyUrl -Url $Matches[1]
}

function Test-ExplicitBadHtml {
    param([Parameter(Mandatory)][string]$Path)

    try {
        $text = Get-TextPrefix -Path $Path -MaxBytes 16384
    }
    catch {
        return $false
    }

    return $text -match 'Twitch Error' -or $text -match 'misconfigured'
}

function Test-RecoveredFileUsable {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    $item = Get-Item -LiteralPath $Path
    if ($item.Length -le 0) {
        return $false
    }

    if ($Path.EndsWith(".json", [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($null -ne (Get-InvalidResourceRetryUrl -Path $Path)) {
            return $false
        }
    }

    $detectedExtension = Get-DetectedExtension -Path $Path
    if ($detectedExtension -eq ".html" -and (Test-ExplicitBadHtml -Path $Path)) {
        return $false
    }

    return $true
}

function Test-RetryUrlLooksLikeMedia {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Kind
    )

    if ($Kind -eq "invalid-resource-json") {
        return $true
    }

    return $Url -match '\.(png|jpe?g|gif|webp|svg|mp4|webm|pdf|mp3|wav|ogg)(\?|$)'
}

function Add-CanonicalMappings {
    param(
        $Node,
        [Parameter(Mandatory)][hashtable]$Map
    )

    if ($null -eq $Node) {
        return
    }

    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        $props = $Node.PSObject.Properties

        $urlProp = $props["url"]
        $canonicalProp = $props["canonicalUrl"]
        if ($null -ne $urlProp -and $null -ne $canonicalProp) {
            $localPath = $urlProp.Value
            $canonicalUrl = $canonicalProp.Value
            if ($localPath -is [string] -and $canonicalUrl -is [string] -and
                $localPath -match '\\Media\\' -and $canonicalUrl -match '^https?://') {
                if (-not $Map.ContainsKey($localPath)) {
                    $Map[$localPath] = New-Object System.Collections.Generic.List[string]
                }
                if (-not $Map[$localPath].Contains($canonicalUrl)) {
                    $Map[$localPath].Add($canonicalUrl)
                }
            }
        }

        foreach ($prop in $props) {
            Add-CanonicalMappings -Node $prop.Value -Map $Map
        }

        return
    }

    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        foreach ($item in $Node) {
            Add-CanonicalMappings -Node $item -Map $Map
        }
    }
}

function Update-References {
    param(
        $Node,
        [Parameter(Mandatory)][hashtable]$ReplacementMap,
        [ref]$Changed
    )

    if ($null -eq $Node) {
        return
    }

    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($prop in $Node.PSObject.Properties) {
            $value = $prop.Value
            if ($value -is [string]) {
                if ($ReplacementMap.ContainsKey($value)) {
                    $prop.Value = $ReplacementMap[$value]
                    $Changed.Value = $true
                }
            }
            elseif ($value -is [System.Collections.IList]) {
                for ($i = 0; $i -lt $value.Count; $i++) {
                    $item = $value[$i]
                    if ($item -is [string]) {
                        if ($ReplacementMap.ContainsKey($item)) {
                            $value[$i] = $ReplacementMap[$item]
                            $Changed.Value = $true
                        }
                    }
                    else {
                        Update-References -Node $item -ReplacementMap $ReplacementMap -Changed $Changed
                    }
                }
            }
            else {
                Update-References -Node $value -ReplacementMap $ReplacementMap -Changed $Changed
            }
        }

        return
    }

    if ($Node -is [System.Collections.IList]) {
        for ($i = 0; $i -lt $Node.Count; $i++) {
            $item = $Node[$i]
            if ($item -is [string]) {
                if ($ReplacementMap.ContainsKey($item)) {
                    $Node[$i] = $ReplacementMap[$item]
                    $Changed.Value = $true
                }
            }
            else {
                Update-References -Node $item -ReplacementMap $ReplacementMap -Changed $Changed
            }
        }
    }
}

function Collect-MissingMediaReferences {
    param(
        $Node,
        [Parameter(Mandatory)][hashtable]$MissingMap
    )

    if ($null -eq $Node) {
        return
    }

    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($prop in $Node.PSObject.Properties) {
            $value = $prop.Value
            if ($value -is [string]) {
                if (($value -match '\\\.workspace\\Discord\\' -or $value -match '\\\.workspace\\discord\\') -and
                    $value -match '\\Media\\' -and
                    -not (Test-Path -LiteralPath $value)) {
                    $MissingMap[$value] = $true
                }
            }
            elseif ($value -is [System.Collections.IEnumerable] -and $value -isnot [string]) {
                foreach ($item in $value) {
                    Collect-MissingMediaReferences -Node $item -MissingMap $MissingMap
                }
            }
            else {
                Collect-MissingMediaReferences -Node $value -MissingMap $MissingMap
            }
        }

        return
    }

    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        foreach ($item in $Node) {
            Collect-MissingMediaReferences -Node $item -MissingMap $MissingMap
        }
    }
}

function Resolve-ExistingMediaAlternative {
    param([Parameter(Mandatory)][string]$MissingPath)

    $directory = Split-Path -Path $MissingPath -Parent
    if (-not (Test-Path -LiteralPath $directory)) {
        return $null
    }

    $fileName = Split-Path -Path $MissingPath -Leaf
    $knownExtPattern = '(?i)(\.(png|jpe?g|gif|webp|svg|mp4|webm|mov|pdf|mp3|wav|ogg|json|html|txt|log|lss))(?:(?:%3A|:).+)$'
    if ($fileName -match $knownExtPattern) {
        $trimmedName = $fileName -replace $knownExtPattern, '$1'
        $trimmedPath = Join-Path $directory $trimmedName
        if (Test-Path -LiteralPath $trimmedPath) {
            return $trimmedPath
        }
    }

    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($fileName)
    if ([string]::IsNullOrWhiteSpace($baseName)) {
        return $null
    }

    $matches = @(Get-ChildItem -LiteralPath $directory -File -Filter ($baseName + '.*') -ErrorAction SilentlyContinue)
    if ($matches.Count -eq 1) {
        return $matches[0].FullName
    }

    if ($matches.Count -gt 1) {
        $preferred = $matches | Where-Object {
            $_.Extension -match '^\.(png|jpe?g|gif|webp|svg|mp4|webm|mov|pdf|mp3|wav|ogg)$'
        }
        if (@($preferred).Count -eq 1) {
            return @($preferred)[0].FullName
        }
    }

    return $null
}

function Save-Json {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Object
    )

    $json = $Object | ConvertTo-Json -Depth 100
    Set-Content -LiteralPath $Path -Value ($json + [Environment]::NewLine) -Encoding utf8NoBOM
}

function Invoke-RetryDownload {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Destination,
        [int]$TimeoutSec = 30
    )

    $tempPath = $Destination + ".download"
    if (Test-Path -LiteralPath $tempPath) {
        Remove-Item -LiteralPath $tempPath -Force
    }

    $headers = @{
        "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0 Safari/537.36"
    }

    try {
        Invoke-WebRequest -Uri $Url -Headers $headers -MaximumRedirection 10 -TimeoutSec $TimeoutSec -OutFile $tempPath | Out-Null
    }
    catch {
        if (Test-Path -LiteralPath $tempPath) {
            Remove-Item -LiteralPath $tempPath -Force
        }
        throw
    }

    if (-not (Test-RecoveredFileUsable -Path $tempPath)) {
        Remove-Item -LiteralPath $tempPath -Force
        throw "Downloaded content is still invalid"
    }

    return $tempPath
}

$rootPath = Get-AbsolutePath -Path $Root
if (-not (Test-Path -LiteralPath $rootPath)) {
    throw "Discord root not found: $rootPath"
}

$exportJsonPaths = Get-ChildItem -LiteralPath $rootPath -Recurse -File -Filter *.json |
    Where-Object {
        $_.FullName -notmatch '\\Media\\' -and
        $_.FullName -notmatch '_Files' -and
        $_.Name -ne 'config.local.json'
    } |
    Select-Object -ExpandProperty FullName

$canonicalMap = @{}
foreach ($jsonPath in $exportJsonPaths) {
    try {
        $json = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
        Add-CanonicalMappings -Node $json -Map $canonicalMap
    }
    catch {
        Write-Warning "Skipping canonical scan for ${jsonPath}: $($_.Exception.Message)"
    }
}

$candidates = New-Object System.Collections.Generic.List[object]
foreach ($guildDir in Get-ChildItem -LiteralPath $rootPath -Directory) {
    $mediaDir = Join-Path $guildDir.FullName "Media"
    if (-not (Test-Path -LiteralPath $mediaDir)) {
        continue
    }

    foreach ($file in Get-ChildItem -LiteralPath $mediaDir -Recurse -File) {
        $kind = $null
        $retryUrls = New-Object System.Collections.Generic.List[string]

        if ($file.Length -eq 0) {
            $kind = "zero-byte"
        }
        else {
            $invalidResourceUrl = Get-InvalidResourceRetryUrl -Path $file.FullName
            if ($null -ne $invalidResourceUrl) {
                $kind = "invalid-resource-json"
                $retryUrls.Add($invalidResourceUrl)
            }
            elseif ((Get-DetectedExtension -Path $file.FullName) -eq ".html" -and (Test-ExplicitBadHtml -Path $file.FullName)) {
                $kind = "html-error"
            }
        }

        if ($null -eq $kind) {
            continue
        }

        if ($canonicalMap.ContainsKey($file.FullName)) {
            foreach ($canonicalUrl in $canonicalMap[$file.FullName]) {
                if (-not [string]::IsNullOrWhiteSpace($canonicalUrl) -and
                    (Test-RetryUrlLooksLikeMedia -Url $canonicalUrl -Kind $kind) -and
                    -not $retryUrls.Contains($canonicalUrl)) {
                    $retryUrls.Add($canonicalUrl)
                }
            }
        }

        $relativeToMedia = [System.IO.Path]::GetRelativePath($mediaDir, $file.FullName)
        $candidates.Add([pscustomobject]@{
            GuildRoot = $guildDir.FullName
            MediaDir = $mediaDir
            Path = $file.FullName
            RelativeToMedia = $relativeToMedia
            Kind = $kind
            RetryUrls = @($retryUrls)
        })
    }
}

$replacementMap = @{}
$results = New-Object System.Collections.Generic.List[object]

$retryableLookup = @{}
$retryableCandidates = @($candidates | Where-Object { $_.RetryUrls.Count -gt 0 })
foreach ($candidate in $retryableCandidates) {
    $retryableLookup[$candidate.Path] = $candidate
}

$downloadResults = @()
if ($retryableCandidates.Count -gt 0) {
    $downloadResults = $retryableCandidates | ForEach-Object -Parallel {
        $candidate = $_
        $headers = @{
            "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0 Safari/537.36"
        }

        foreach ($retryUrl in $candidate.RetryUrls) {
            $tempPath = $candidate.Path + ".download"
            if (Test-Path -LiteralPath $tempPath) {
                Remove-Item -LiteralPath $tempPath -Force
            }

            try {
                Invoke-WebRequest -Uri $retryUrl -Headers $headers -MaximumRedirection 10 -TimeoutSec $using:TimeoutSec -OutFile $tempPath | Out-Null

                if (-not (Test-Path -LiteralPath $tempPath)) {
                    throw "Download did not produce a file"
                }

                $item = Get-Item -LiteralPath $tempPath
                if ($item.Length -le 0) {
                    throw "Downloaded content is empty"
                }

                $bytes = [System.IO.File]::ReadAllBytes($tempPath)
                $text = [System.Text.Encoding]::UTF8.GetString($bytes, 0, [Math]::Min($bytes.Length, 16384))
                if ($text -match '^\s*\{"message":"Invalid resource ') {
                    throw "Downloaded content is still an invalid-resource payload"
                }
                if ($text -match 'Twitch Error' -or $text -match 'misconfigured') {
                    throw "Downloaded content is still an embed error page"
                }

                return [pscustomobject]@{
                    Path = $candidate.Path
                    Success = $true
                    Detail = $retryUrl
                    TempPath = $tempPath
                }
            }
            catch {
                if (Test-Path -LiteralPath $tempPath) {
                    Remove-Item -LiteralPath $tempPath -Force
                }
                $lastError = "$retryUrl => $($_.Exception.Message)"
            }
        }

        return [pscustomobject]@{
            Path = $candidate.Path
            Success = $false
            Detail = $lastError
            TempPath = $null
        }
    } -ThrottleLimit $ThrottleLimit
}

$downloadResultMap = @{}
foreach ($result in $downloadResults) {
    $downloadResultMap[$result.Path] = $result
}

foreach ($candidate in $candidates) {
    $status = "quarantine"
    $detail = $candidate.Kind
    $quarantinePath = Join-Path (Join-Path $candidate.GuildRoot "Quarantine") $candidate.RelativeToMedia
    $recoveredPath = $null

    if ($downloadResultMap.ContainsKey($candidate.Path)) {
        $downloadResult = $downloadResultMap[$candidate.Path]
        if ($downloadResult.Success) {
            if (-not $DryRun) {
                Move-Item -LiteralPath $downloadResult.TempPath -Destination $candidate.Path -Force
            }
            else {
                Remove-Item -LiteralPath $downloadResult.TempPath -Force
            }

            $finalPath = $candidate.Path
            $detectedExtension = if (-not $DryRun) { Get-DetectedExtension -Path $candidate.Path } else { $null }
            $currentExtension = [System.IO.Path]::GetExtension($candidate.Path)

            if (-not $DryRun -and -not [string]::IsNullOrWhiteSpace($detectedExtension) -and
                -not [string]::Equals($currentExtension, $detectedExtension, [System.StringComparison]::OrdinalIgnoreCase)) {
                $renamedPath = if ([string]::IsNullOrWhiteSpace($currentExtension)) {
                    $candidate.Path + $detectedExtension
                }
                else {
                    [System.IO.Path]::ChangeExtension($candidate.Path, $detectedExtension)
                }

                if (Test-Path -LiteralPath $renamedPath) {
                    Remove-Item -LiteralPath $renamedPath -Force
                }

                Move-Item -LiteralPath $candidate.Path -Destination $renamedPath -Force
                $replacementMap[$candidate.Path] = $renamedPath
                $finalPath = $renamedPath
            }

            $status = "recovered"
            $detail = $downloadResult.Detail
            $recoveredPath = $finalPath
        }
        else {
            $detail = "$($candidate.Kind): $($downloadResult.Detail)"
        }
    }

    if ($status -ne "recovered") {
        $replacementMap[$candidate.Path] = $null
    }

    $results.Add([pscustomobject]@{
        Path = $candidate.Path
        Kind = $candidate.Kind
        Status = $status
        Detail = $detail
        QuarantinePath = if ($status -eq "quarantine") { $quarantinePath } else { $null }
        RecoveredPath = $recoveredPath
    })
}

$updatedJsonCount = 0
if ($replacementMap.Count -gt 0) {
    foreach ($jsonPath in $exportJsonPaths) {
        try {
            $json = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
            $changed = $false
            Update-References -Node $json -ReplacementMap $replacementMap -Changed ([ref]$changed)
            if ($changed) {
                if (-not $DryRun) {
                    Save-Json -Path $jsonPath -Object $json
                }
                $updatedJsonCount++
            }
        }
        catch {
            Write-Warning "Failed to update references in ${jsonPath}: $($_.Exception.Message)"
        }
    }
}

if (-not $DryRun) {
    foreach ($result in $results | Where-Object { $_.Status -eq "quarantine" }) {
        Ensure-Directory -Path ([System.IO.Path]::GetDirectoryName($result.QuarantinePath))
        Move-Item -LiteralPath $result.Path -Destination $result.QuarantinePath -Force
    }
}

$missingReferenceReplacementMap = @{}
foreach ($jsonPath in $exportJsonPaths) {
    try {
        $json = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
        $missingRefs = @{}
        Collect-MissingMediaReferences -Node $json -MissingMap $missingRefs

        foreach ($missingPath in $missingRefs.Keys) {
            if ($missingReferenceReplacementMap.ContainsKey($missingPath)) {
                continue
            }

            $resolvedPath = Resolve-ExistingMediaAlternative -MissingPath $missingPath
            if ($null -ne $resolvedPath) {
                $missingReferenceReplacementMap[$missingPath] = $resolvedPath
            }
            else {
                $missingReferenceReplacementMap[$missingPath] = $null
            }
        }
    }
    catch {
        Write-Warning "Failed to scan missing references in ${jsonPath}: $($_.Exception.Message)"
    }
}

if ($missingReferenceReplacementMap.Count -gt 0) {
    foreach ($jsonPath in $exportJsonPaths) {
        try {
            $json = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
            $changed = $false
            Update-References -Node $json -ReplacementMap $missingReferenceReplacementMap -Changed ([ref]$changed)
            if ($changed) {
                if (-not $DryRun) {
                    Save-Json -Path $jsonPath -Object $json
                }
                $updatedJsonCount++
            }
        }
        catch {
            Write-Warning "Failed to reconcile missing references in ${jsonPath}: $($_.Exception.Message)"
        }
    }
}

$byKind = @()
foreach ($group in ($results | Group-Object Kind | Sort-Object Name)) {
    $byKind += [pscustomobject]@{
        Kind = $group.Name
        Count = @($group.Group).Count
        Recovered = @($group.Group | Where-Object Status -eq "recovered").Count
        Quarantined = @($group.Group | Where-Object Status -eq "quarantine").Count
    }
}

$summary = [pscustomobject]@{
    Root = $rootPath
    DryRun = [bool]$DryRun
    CandidateCount = $candidates.Count
    RecoveredCount = ($results | Where-Object Status -eq "recovered").Count
    QuarantinedCount = ($results | Where-Object Status -eq "quarantine").Count
    UpdatedJsonCount = $updatedJsonCount
    ByKind = $byKind
}

$summary | ConvertTo-Json -Depth 5
