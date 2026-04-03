param(
    [string]$GuildRoot,
    [int]$MinConfidence = 85,
    [int]$MaxLocalBytes = 800000,
    [int]$MaxRemoteBytes = 800000,
    [int]$MaxSnippetChars = 16000,
    [int]$TimeoutSec = 30,
    [int]$FetchRetryCount = 0,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-AbsolutePath {
    param([Parameter(Mandatory)][string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Ensure-Directory {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

function Get-MarkdownFence {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return '```' }
    if ($Text -match '````') { return '`````' }
    if ($Text -match '```') { return '````' }
    return '```'
}

function Normalize-Url {
    param([Parameter(Mandatory)][string]$Url)
    $trimmed = $Url.Trim()
    while ($trimmed.Length -gt 0 -and $trimmed[-1] -in @('.', ',', ';', ':', ')', ']', '}', '>')) {
        $trimmed = $trimmed.Substring(0, $trimmed.Length - 1)
    }
    return $trimmed
}

function Get-UrlsFromText {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) {
        return @()
    }

    $urls = [System.Collections.Generic.List[string]]::new()
    $matches = [regex]::Matches($Text, 'https?://[^\s<>"''\]\)]+')
    foreach ($m in $matches) {
        $u = Normalize-Url -Url $m.Value
        if (-not [string]::IsNullOrWhiteSpace($u)) {
            $urls.Add($u)
        }
    }

    return @($urls | Select-Object -Unique)
}

function Test-CodeLikeExtension {
    param([string]$Extension)
    if ([string]::IsNullOrWhiteSpace($Extension)) { return $false }

    $ext = $Extension.ToLowerInvariant()
    $set = @(
        ".txt",".log",".md",".markdown",".rst",".ini",".cfg",".conf",".toml",".yaml",".yml",".json",".jsonc",".xml",".csv",
        ".cs",".csproj",".sln",".c",".h",".cpp",".hpp",".cc",".cxx",".hh",".java",".kt",".kts",".go",".rs",".py",".lua",
        ".js",".jsx",".ts",".tsx",".php",".swift",".vb",".fs",".fsi",".m",".mm",".sh",".bash",".zsh",".ps1",".bat",".cmd",
        ".sql",".patch",".diff",".hexpat",".ebp",".tbl",".dat"
    )
    return $set -contains $ext
}

function Truncate-Text {
    param(
        [string]$Text,
        [int]$MaxChars
    )
    if ($null -eq $Text) {
        return ""
    }
    if ($Text.Length -le $MaxChars) {
        return $Text
    }
    return $Text.Substring(0, $MaxChars) + [Environment]::NewLine + "[TRUNCATED]"
}

function Test-TextLooksBinary {
    param([byte[]]$Bytes)
    if ($null -eq $Bytes -or $Bytes.Length -eq 0) {
        return $false
    }

    $limit = [Math]::Min($Bytes.Length, 12000)
    $nullCount = 0
    for ($i = 0; $i -lt $limit; $i++) {
        if ($Bytes[$i] -eq 0) {
            $nullCount++
        }
    }

    return ($nullCount -gt 0)
}

function Read-TextFileSafe {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$MaxBytes = 800000
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $file = Get-Item -LiteralPath $Path
    if ($file.Length -le 0 -or $file.Length -gt $MaxBytes) {
        return $null
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if (Test-TextLooksBinary -Bytes $bytes) {
        return $null
    }

    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($text -match '(?is)<html[^>]*>') {
        return $null
    }

    return $text
}

function Convert-GitHubBlobToRaw {
    param([Parameter(Mandatory)][string]$Url)
    $u = $Url.Split('#')[0]
    if ($u -match '^https?://github\.com/([^/]+)/([^/]+)/blob/([^/]+)/(.+)$') {
        $owner = $matches[1]
        $repo = $matches[2]
        $ref = $matches[3]
        $path = $matches[4]
        return "https://raw.githubusercontent.com/$owner/$repo/$ref/$path"
    }
    return $null
}

function Convert-GitHubCommitToPatch {
    param([Parameter(Mandatory)][string]$Url)
    $u = $Url.Split('#')[0]
    if ($u -match '^https?://github\.com/([^/]+)/([^/]+)/commit/([0-9a-fA-F]+)$') {
        return "$u.patch"
    }
    return $null
}

function Convert-GitHubPrToPatch {
    param([Parameter(Mandatory)][string]$Url)
    $u = $Url.Split('#')[0]
    if ($u -match '^https?://github\.com/([^/]+)/([^/]+)/pull/([0-9]+)$') {
        return "$u.patch"
    }
    return $null
}

function Convert-GistPageToRaw {
    param([Parameter(Mandatory)][string]$Url)
    $u = $Url.Split('#')[0].TrimEnd('/')
    if ($u -match '^https?://gist\.github\.com/[^/]+/[0-9a-fA-F]+(?:/[^/?#]+)?$') {
        return "$u/raw"
    }
    return $null
}

function Resolve-RemoteFetchTarget {
    param([Parameter(Mandatory)][string]$Url)
    $u = $Url.Split('#')[0]

    $blob = Convert-GitHubBlobToRaw -Url $u
    if ($null -ne $blob) {
        return [pscustomobject]@{ FetchUrl = $blob; SourceType = "github_blob_raw" }
    }

    $commitPatch = Convert-GitHubCommitToPatch -Url $u
    if ($null -ne $commitPatch) {
        return [pscustomobject]@{ FetchUrl = $commitPatch; SourceType = "github_commit_patch" }
    }

    $prPatch = Convert-GitHubPrToPatch -Url $u
    if ($null -ne $prPatch) {
        return [pscustomobject]@{ FetchUrl = $prPatch; SourceType = "github_pr_patch" }
    }

    $gistRaw = Convert-GistPageToRaw -Url $u
    if ($null -ne $gistRaw) {
        return [pscustomobject]@{ FetchUrl = $gistRaw; SourceType = "gist_raw" }
    }

    if ($u -match '^https?://raw\.githubusercontent\.com/' -or $u -match '^https?://gist\.githubusercontent\.com/') {
        return [pscustomobject]@{ FetchUrl = $u; SourceType = "raw_text_url" }
    }

    $path = ""
    try {
        $uri = [Uri]$u
        $path = $uri.AbsolutePath
    }
    catch {
        return $null
    }

    $ext = [System.IO.Path]::GetExtension($path).ToLowerInvariant()
    if (Test-CodeLikeExtension -Extension $ext) {
        return [pscustomobject]@{ FetchUrl = $u; SourceType = "direct_code_url" }
    }

    return $null
}

function Fetch-RemoteText {
    param(
        [Parameter(Mandatory)][string]$Url,
        [int]$TimeoutSec = 30,
        [int]$MaxBytes = 800000,
        [int]$FetchRetryCount = 0
    )

    $headers = @{ "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Fahrenheit-Discord-Embed/1.0" }
    $maxAttempts = [Math]::Max(1, $FetchRetryCount + 1)
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("embed_remote_" + [Guid]::NewGuid().ToString("N") + ".tmp")
        try {
            Invoke-WebRequest -Uri $Url -TimeoutSec $TimeoutSec -MaximumRedirection 10 -Headers $headers -OutFile $tmp | Out-Null
            if (-not (Test-Path -LiteralPath $tmp)) {
                return $null
            }

            $file = Get-Item -LiteralPath $tmp
            if ($file.Length -le 0 -or $file.Length -gt $MaxBytes) {
                return $null
            }

            $bytes = [System.IO.File]::ReadAllBytes($tmp)
            if (Test-TextLooksBinary -Bytes $bytes) {
                return $null
            }

            $text = [System.Text.Encoding]::UTF8.GetString($bytes)
            if ($text -match '(?is)<html[^>]*>') {
                return $null
            }

            return $text
        }
        catch {
            if ($attempt -ge $maxAttempts) {
                return $null
            }

            Start-Sleep -Seconds ([Math]::Min(5, $attempt * 2))
        }
        finally {
            if (Test-Path -LiteralPath $tmp) {
                Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            }
        }
    }

    return $null
}

function Remove-ExistingEmbedBlock {
    param(
        [string]$Content,
        [Parameter(Mandatory)][string]$StartMarker,
        [Parameter(Mandatory)][string]$EndMarker
    )

    if ($null -eq $Content) { return "" }
    $pattern = [regex]::Escape($StartMarker) + '.*?' + [regex]::Escape($EndMarker)
    return [regex]::Replace($Content, $pattern, "", [System.Text.RegularExpressions.RegexOptions]::Singleline).TrimEnd()
}

if ([string]::IsNullOrWhiteSpace($GuildRoot)) {
    throw "GuildRoot is required."
}

$guildRootPath = Get-AbsolutePath -Path $GuildRoot
if (-not (Test-Path -LiteralPath $guildRootPath)) {
    throw "GuildRoot does not exist: $guildRootPath"
}

$startMarker = "<!-- BEGIN EMBEDDED_CODE_SNIPPETS -->"
$endMarker = "<!-- END EMBEDDED_CODE_SNIPPETS -->"
$backupRoot = Join-Path $guildRootPath "Quarantine\\Backup-Before-CodeLinkEmbed"
if (-not $DryRun) {
    Ensure-Directory -Path $backupRoot
}

$jsonFiles = Get-ChildItem -LiteralPath $guildRootPath -Recurse -File -Filter *.json | Where-Object { $_.FullName -notmatch '\\Media\\' }
$remoteCache = @{}
$changes = [System.Collections.Generic.List[object]]::new()

$totalFilesChanged = 0
$totalMessagesChanged = 0
$totalLocalAttachmentsEmbedded = 0
$totalLocalAttachmentsRemoved = 0
$totalRemoteLinksEmbedded = 0
$totalRemoteFetches = 0
$totalRemoteCacheHits = 0

foreach ($file in $jsonFiles) {
    $relExport = [System.IO.Path]::GetRelativePath($guildRootPath, $file.FullName)
    try {
        $json = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    }
    catch {
        continue
    }

    $messagesProp = $json.PSObject.Properties["messages"]
    if ($null -eq $messagesProp) {
        continue
    }

    $messages = @($messagesProp.Value)
    if ($messages.Count -eq 0) {
        continue
    }

    $fileMessagesChanged = 0
    $fileLocalAttachmentsEmbedded = 0
    $fileLocalAttachmentsRemoved = 0
    $fileRemoteLinksEmbedded = 0

    foreach ($message in $messages) {
        $msgSnippets = [System.Collections.Generic.List[string]]::new()
        $usedLocalAttachmentUrls = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

        foreach ($attachment in @($message.attachments)) {
            $attUrl = [string]$attachment.url
            if ([string]::IsNullOrWhiteSpace($attUrl)) { continue }

            $ext = [System.IO.Path]::GetExtension($attUrl).ToLowerInvariant()
            if (-not (Test-CodeLikeExtension -Extension $ext)) { continue }

            $text = Read-TextFileSafe -Path $attUrl -MaxBytes $MaxLocalBytes
            if ([string]::IsNullOrWhiteSpace($text)) { continue }

            $text = Truncate-Text -Text $text -MaxChars $MaxSnippetChars
            $fence = Get-MarkdownFence -Text $text
            $name = if (-not [string]::IsNullOrWhiteSpace([string]$attachment.fileName)) { [string]$attachment.fileName } else { [System.IO.Path]::GetFileName($attUrl) }
            $snippet = @(
                "[Embedded local attachment: $name]"
                ($fence + "text")
                $text
                $fence
            ) -join [Environment]::NewLine
            $msgSnippets.Add($snippet)
            [void]$usedLocalAttachmentUrls.Add($attUrl)
            $fileLocalAttachmentsEmbedded++
        }

        $urls = [System.Collections.Generic.List[string]]::new()
        foreach ($u in (Get-UrlsFromText -Text ([string]$message.content))) {
            $urls.Add($u)
        }
        foreach ($embed in @($message.embeds)) {
            $eu = [string]$embed.url
            if (-not [string]::IsNullOrWhiteSpace($eu)) {
                $urls.Add((Normalize-Url -Url $eu))
            }
        }

        foreach ($url in @($urls | Select-Object -Unique)) {
            $target = Resolve-RemoteFetchTarget -Url $url
            if ($null -eq $target) {
                continue
            }

            $cacheKey = [string]$target.FetchUrl
            $cached = $null
            if ($remoteCache.ContainsKey($cacheKey)) {
                $cached = $remoteCache[$cacheKey]
                $totalRemoteCacheHits++
            }
            else {
                $fetched = Fetch-RemoteText -Url $target.FetchUrl -TimeoutSec $TimeoutSec -MaxBytes $MaxRemoteBytes -FetchRetryCount $FetchRetryCount
                $totalRemoteFetches++
                $cached = [pscustomobject]@{
                    Ok = (-not [string]::IsNullOrWhiteSpace($fetched))
                    Text = $fetched
                    FetchUrl = [string]$target.FetchUrl
                    SourceType = [string]$target.SourceType
                }
                $remoteCache[$cacheKey] = $cached
            }

            if (-not [bool]$cached.Ok) {
                continue
            }

            $remoteText = Truncate-Text -Text ([string]$cached.Text) -MaxChars $MaxSnippetChars
            if ([string]::IsNullOrWhiteSpace($remoteText)) {
                continue
            }

            $fence = Get-MarkdownFence -Text $remoteText
            $snippet = @(
                "[Embedded remote source: $url]"
                ($fence + "text")
                $remoteText
                $fence
            ) -join [Environment]::NewLine
            $msgSnippets.Add($snippet)
            $fileRemoteLinksEmbedded++
        }

        if ($msgSnippets.Count -eq 0) {
            continue
        }

        $cleanContent = Remove-ExistingEmbedBlock -Content ([string]$message.content) -StartMarker $startMarker -EndMarker $endMarker
        $embedBlock = @(
            $startMarker
            "Extracted code/text snippets:"
            ($msgSnippets -join ([Environment]::NewLine + [Environment]::NewLine))
            $endMarker
        ) -join [Environment]::NewLine

        if ([string]::IsNullOrWhiteSpace($cleanContent)) {
            $message.content = $embedBlock
        }
        else {
            $message.content = $cleanContent + [Environment]::NewLine + [Environment]::NewLine + $embedBlock
        }

        if ($usedLocalAttachmentUrls.Count -gt 0) {
            $before = @($message.attachments).Count
            $message.attachments = @(
                @($message.attachments) | Where-Object {
                    $u = [string]$_.url
                    -not $usedLocalAttachmentUrls.Contains($u)
                }
            )
            $after = @($message.attachments).Count
            $fileLocalAttachmentsRemoved += ($before - $after)
        }

        $fileMessagesChanged++
    }

    if ($fileMessagesChanged -gt 0) {
        if (-not $DryRun) {
            $backupPath = Join-Path $backupRoot ($relExport -replace '[\\/]', '__')
            if (-not (Test-Path -LiteralPath $backupPath)) {
                Copy-Item -LiteralPath $file.FullName -Destination $backupPath -Force
            }

            $out = $json | ConvertTo-Json -Depth 100
            Set-Content -LiteralPath $file.FullName -Value ($out + [Environment]::NewLine) -Encoding utf8NoBOM
        }

        $status = if ($DryRun) { "dryrun_would_update" } else { "updated" }
        $changes.Add([pscustomobject]@{
            ExportFile = $relExport
            Status = $status
            MessagesChanged = $fileMessagesChanged
            LocalAttachmentsEmbedded = $fileLocalAttachmentsEmbedded
            LocalAttachmentsRemoved = $fileLocalAttachmentsRemoved
            RemoteLinksEmbedded = $fileRemoteLinksEmbedded
        })

        $totalFilesChanged++
        $totalMessagesChanged += $fileMessagesChanged
        $totalLocalAttachmentsEmbedded += $fileLocalAttachmentsEmbedded
        $totalLocalAttachmentsRemoved += $fileLocalAttachmentsRemoved
        $totalRemoteLinksEmbedded += $fileRemoteLinksEmbedded
    }
}

$summary = [pscustomobject]@{
    UpdatedAt = (Get-Date).ToString("s")
    GuildRoot = $guildRootPath
    DryRun = [bool]$DryRun
    MinConfidence = $MinConfidence
    FilesChanged = $totalFilesChanged
    MessagesChanged = $totalMessagesChanged
    LocalAttachmentsEmbedded = $totalLocalAttachmentsEmbedded
    LocalAttachmentsRemoved = $totalLocalAttachmentsRemoved
    RemoteLinksEmbedded = $totalRemoteLinksEmbedded
    RemoteFetches = $totalRemoteFetches
    RemoteCacheHits = $totalRemoteCacheHits
}

$guildFolder = Split-Path -Path $guildRootPath -Leaf
$analysisDir = Join-Path ".workspace\\analysis\\discord" (Join-Path $guildFolder "embed-code")
Ensure-Directory -Path $analysisDir
$summaryPath = Join-Path $analysisDir "embed_code_links.summary.json"
$changesPath = Join-Path $analysisDir "embed_code_links.file_changes.jsonl"

Set-Content -LiteralPath $summaryPath -Value (($summary | ConvertTo-Json -Depth 20) + [Environment]::NewLine) -Encoding utf8NoBOM
if (Test-Path -LiteralPath $changesPath) {
    Remove-Item -LiteralPath $changesPath -Force
}
foreach ($c in $changes) {
    Add-Content -LiteralPath $changesPath -Value (($c | ConvertTo-Json -Depth 20 -Compress) + [Environment]::NewLine) -Encoding utf8NoBOM
}

Write-Output ("Embedding complete. Summary: {0}" -f $summaryPath)
Write-Output ("Per-file changes: {0}" -f $changesPath)
if (-not $DryRun) {
    Write-Output ("Backups: {0}" -f $backupRoot)
}
