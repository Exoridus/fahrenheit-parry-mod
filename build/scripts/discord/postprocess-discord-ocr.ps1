param(
    [string]$GuildRoot,
    [string]$OcrOutDir,
    [string]$QuarantineSubdir = "OCR-BadRequest",
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

function Read-JsonLines {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $rows.Add(($line | ConvertFrom-Json))
        }
        catch {
        }
    }

    return @($rows)
}

function Write-JsonLines {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Rows
    )

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Force
    }

    foreach ($row in $Rows) {
        Add-Content -LiteralPath $Path -Value (($row | ConvertTo-Json -Depth 20 -Compress) + [Environment]::NewLine) -Encoding utf8NoBOM
    }
}

function Get-RelativeToRoot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root
    )

    try {
        return [System.IO.Path]::GetRelativePath($Root, $Path)
    }
    catch {
        return $Path
    }
}

function Test-LikelyCodeText {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $false
    }

    $lineCount = (($Text -split "`r?`n") | Measure-Object).Count
    $score = 0

    if ($lineCount -ge 3) { $score++ }
    if ($Text -match '[{};]') { $score++ }
    if ($Text -match '\b(class|struct|void|public|private|if|else|for|while|return|using|namespace|def|function|import)\b') { $score++ }
    if ($Text -match '(^|`n)\s*(PS [A-Z]:\\|[A-Za-z0-9_.-]+@[^ ]+[:~]\$|#include|```|Traceback|Exception)') { $score++ }
    if ($Text -match '\b(error|warning|stack|line \d+|cannot|failed|command not found)\b') { $score++ }

    return ($score -ge 2)
}

if ([string]::IsNullOrWhiteSpace($GuildRoot)) {
    throw "GuildRoot is required."
}

if ([string]::IsNullOrWhiteSpace($OcrOutDir)) {
    throw "OcrOutDir is required."
}

$guildRootPath = Get-AbsolutePath -Path $GuildRoot
$ocrOutPath = Get-AbsolutePath -Path $OcrOutDir
$mediaRoot = Join-Path $guildRootPath "Media"
$quarantineRoot = Join-Path $guildRootPath "Quarantine"
$quarantineTargetRoot = Join-Path $quarantineRoot $QuarantineSubdir

if (-not (Test-Path -LiteralPath $guildRootPath)) {
    throw "GuildRoot does not exist: $guildRootPath"
}

if (-not (Test-Path -LiteralPath $ocrOutPath)) {
    throw "OcrOutDir does not exist: $ocrOutPath"
}

if (-not (Test-Path -LiteralPath $mediaRoot)) {
    throw "Media directory not found: $mediaRoot"
}

$manifestPath = Join-Path $ocrOutPath "manifest.json"
$pass1Path = Join-Path $ocrOutPath "pass1.jsonl"
$pass2Path = Join-Path $ocrOutPath "pass2.jsonl"

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Missing manifest: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$pass1 = Read-JsonLines -Path $pass1Path
$pass2 = Read-JsonLines -Path $pass2Path

$manifestByPath = @{}
foreach ($item in $manifest) {
    $manifestByPath[[string]$item.Path] = $item
}

$pass1ByPath = @{}
foreach ($item in $pass1) {
    $pass1ByPath[[string]$item.Path] = $item
}

$pass2ByPath = @{}
foreach ($item in $pass2) {
    $pass2ByPath[[string]$item.Path] = $item
}

$selectedManifest = @($manifest | Where-Object { $_.Selected })

$imageRows = [System.Collections.Generic.List[object]]::new()
$messageRows = [System.Collections.Generic.List[object]]::new()
$messageIndex = @{}
$badRequestRows = [System.Collections.Generic.List[object]]::new()

foreach ($m in $selectedManifest) {
    $path = [string]$m.Path
    $relToGuild = Get-RelativeToRoot -Path $path -Root $guildRootPath
    $pass = 0
    $model = $null
    $isComplete = $null
    $classification = $null
    $confidence = 0
    $notes = ""
    $text = ""

    if ($pass2ByPath.ContainsKey($path)) {
        $p2 = $pass2ByPath[$path]
        $pass = 2
        $model = [string]$p2.Model
        $isComplete = [bool]$p2.Complete
        $confidence = [int]$p2.Confidence
        $notes = [string]$p2.Notes
        $text = [string]$p2.Text

        if ($isComplete -and -not [string]::IsNullOrWhiteSpace($text)) {
            $classification = "full_text"
        }
        elseif (-not [string]::IsNullOrWhiteSpace($text)) {
            $classification = "partial_text"
        }
        elseif ($isComplete) {
            $classification = "no_text"
        }
        else {
            $classification = "error"
        }

        if ($notes -like "*400 (Bad Request)*") {
            $badRequestRows.Add([pscustomobject]@{
                Path = $path
                RelativePath = $relToGuild
                Extension = [string]$m.Extension
                Bytes = [int64]$m.Bytes
                Width = [int]$m.Width
                Height = [int]$m.Height
                Notes = $notes
                Confidence = $confidence
            })
        }
    }
    elseif ($pass1ByPath.ContainsKey($path)) {
        $p1 = $pass1ByPath[$path]
        $pass = 1
        $model = [string]$p1.Model
        $isComplete = $null
        $classification = [string]$p1.Classification
        $confidence = [int]$p1.Confidence
        $notes = [string]$p1.Notes
        $text = [string]$p1.Text
    }
    else {
        $pass = 0
        $classification = "missing"
        $confidence = 0
        $notes = "No OCR row found"
        $text = ""
    }

    $looksLikeCode = Test-LikelyCodeText -Text $text
    $textLineCount = if ([string]::IsNullOrWhiteSpace($text)) { 0 } else { (($text -split "`r?`n") | Measure-Object).Count }
    $textLength = if ($null -eq $text) { 0 } else { $text.Length }
    $sourceRefs = @($m.SourceRefs)

    $imageResult = [pscustomobject]@{
        ImagePath = $path
        ImagePathRelative = $relToGuild
        Pass = $pass
        Model = $model
        Classification = $classification
        Complete = $isComplete
        Confidence = $confidence
        Notes = $notes
        Text = $text
        TextLength = $textLength
        TextLineCount = $textLineCount
        LooksLikeCode = $looksLikeCode
        SourceRefCount = @($sourceRefs).Count
        SourceKinds = @($m.SourceKinds)
    }
    $imageRows.Add($imageResult)

    foreach ($ref in $sourceRefs) {
        $exportFile = [string]$ref.ExportFile
        $channelId = [string]$ref.ChannelId
        $messageId = [string]$ref.MessageId
        $messageKey = "$exportFile|$channelId|$messageId"

        $messageRow = [pscustomobject]@{
            MessageKey = $messageKey
            ExportFile = $exportFile
            GuildId = [string]$ref.GuildId
            GuildName = [string]$ref.GuildName
            ChannelId = $channelId
            ChannelName = [string]$ref.ChannelName
            MessageId = $messageId
            MessageTimestamp = [string]$ref.MessageTimestamp
            AuthorId = [string]$ref.AuthorId
            AuthorName = [string]$ref.AuthorName
            SourceKind = [string]$ref.SourceKind
            AttachmentIndex = [int]$ref.AttachmentIndex
            EmbedIndex = [int]$ref.EmbedIndex
            EmbedImageIndex = [int]$ref.EmbedImageIndex
            ImagePath = $path
            ImagePathRelative = $relToGuild
            OcrPass = $pass
            OcrModel = $model
            OcrClassification = $classification
            OcrComplete = $isComplete
            OcrConfidence = $confidence
            OcrNotes = $notes
            OcrText = $text
            OcrTextLength = $textLength
            OcrTextLineCount = $textLineCount
            OcrLooksLikeCode = $looksLikeCode
        }
        $messageRows.Add($messageRow)

        if (-not $messageIndex.ContainsKey($messageKey)) {
            $messageIndex[$messageKey] = [System.Collections.Generic.List[object]]::new()
        }
        $messageIndex[$messageKey].Add($messageRow)
    }
}

$moveResults = [System.Collections.Generic.List[object]]::new()
if ($badRequestRows.Count -gt 0) {
    Ensure-Directory -Path $quarantineTargetRoot
}

foreach ($row in $badRequestRows) {
    $sourcePath = [string]$row.Path
    $status = "missing"
    $destPath = $null
    $errorText = $null

    try {
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            $status = "missing"
        }
        else {
            $sourcePathNorm = [System.IO.Path]::GetFullPath($sourcePath)
            $mediaRootNorm = [System.IO.Path]::GetFullPath($mediaRoot)
            if (-not $sourcePathNorm.StartsWith($mediaRootNorm, [System.StringComparison]::OrdinalIgnoreCase)) {
                $status = "skipped_outside_media"
            }
            else {
                $relativeFromMedia = [System.IO.Path]::GetRelativePath($mediaRootNorm, $sourcePathNorm)
                $destPath = Join-Path $quarantineTargetRoot $relativeFromMedia
                $destDir = Split-Path -Path $destPath -Parent
                Ensure-Directory -Path $destDir

                if (Test-Path -LiteralPath $destPath) {
                    $base = [System.IO.Path]::GetFileNameWithoutExtension($destPath)
                    $ext = [System.IO.Path]::GetExtension($destPath)
                    $dir = Split-Path -Path $destPath -Parent
                    $destPath = Join-Path $dir ($base + "__dup_" + [DateTime]::UtcNow.ToString("yyyyMMddHHmmssfff") + $ext)
                }

                if ($DryRun) {
                    $status = "dryrun_would_move"
                }
                else {
                    Move-Item -LiteralPath $sourcePath -Destination $destPath
                    $status = "moved"
                }
            }
        }
    }
    catch {
        $status = "move_failed"
        $errorText = $_.Exception.Message
    }

    $moveResults.Add([pscustomobject]@{
        Path = $sourcePath
        RelativePath = [string]$row.RelativePath
        DestinationPath = $destPath
        Status = $status
        Error = $errorText
        Notes = [string]$row.Notes
    })
}

$messageIndexRows = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $messageIndex.GetEnumerator()) {
    $rows = @($entry.Value)
    $first = $rows[0]
    $hasCode = @($rows | Where-Object { $_.OcrLooksLikeCode }).Count -gt 0
    $goodText = @($rows | Where-Object { $_.OcrClassification -in @("full_text", "partial_text") -and -not [string]::IsNullOrWhiteSpace($_.OcrText) })

    $messageIndexRows.Add([pscustomobject]@{
        MessageKey = $entry.Key
        ExportFile = $first.ExportFile
        GuildId = $first.GuildId
        GuildName = $first.GuildName
        ChannelId = $first.ChannelId
        ChannelName = $first.ChannelName
        MessageId = $first.MessageId
        MessageTimestamp = $first.MessageTimestamp
        AuthorId = $first.AuthorId
        AuthorName = $first.AuthorName
        ReferencedImageCount = $rows.Count
        OcrWithTextCount = $goodText.Count
        HasLikelyCodeScreenshot = $hasCode
        ImageRows = $rows
    })
}

$finalSummary = [pscustomobject]@{
    UpdatedAt = (Get-Date).ToString("s")
    GuildRoot = $guildRootPath
    OcrOutDir = $ocrOutPath
    DryRun = [bool]$DryRun
    SelectedImages = $selectedManifest.Count
    ImageResultsCount = $imageRows.Count
    MessageImageLinksCount = $messageRows.Count
    MessageGroupsCount = $messageIndexRows.Count
    BadRequestCount = $badRequestRows.Count
    Quarantine = [pscustomobject]@{
        TargetRoot = $quarantineTargetRoot
        Moved = @($moveResults | Where-Object { $_.Status -eq "moved" }).Count
        DryRunWouldMove = @($moveResults | Where-Object { $_.Status -eq "dryrun_would_move" }).Count
        Missing = @($moveResults | Where-Object { $_.Status -eq "missing" }).Count
        OutsideMedia = @($moveResults | Where-Object { $_.Status -eq "skipped_outside_media" }).Count
        Failed = @($moveResults | Where-Object { $_.Status -eq "move_failed" }).Count
    }
}

$summaryPath = Join-Path $ocrOutPath "postprocess.summary.json"
$imageResultsPath = Join-Path $ocrOutPath "ocr_image_results.jsonl"
$messageLinksPath = Join-Path $ocrOutPath "ocr_message_image_links.jsonl"
$messageIndexPath = Join-Path $ocrOutPath "ocr_message_index.json"
$badRequestPath = Join-Path $ocrOutPath "quarantine.bad_request.jsonl"
$moveResultsPath = Join-Path $ocrOutPath "quarantine.move_results.jsonl"

Set-Content -LiteralPath $summaryPath -Value (($finalSummary | ConvertTo-Json -Depth 20) + [Environment]::NewLine) -Encoding utf8NoBOM
Write-JsonLines -Path $imageResultsPath -Rows $imageRows
Write-JsonLines -Path $messageLinksPath -Rows $messageRows
Set-Content -LiteralPath $messageIndexPath -Value ((@($messageIndexRows | Sort-Object MessageTimestamp,MessageId) | ConvertTo-Json -Depth 20) + [Environment]::NewLine) -Encoding utf8NoBOM
Write-JsonLines -Path $badRequestPath -Rows $badRequestRows
Write-JsonLines -Path $moveResultsPath -Rows $moveResults

Write-Output ("Postprocess complete. Summary: {0}" -f $summaryPath)
Write-Output ("Image results: {0}" -f $imageResultsPath)
Write-Output ("Message/image links: {0}" -f $messageLinksPath)
Write-Output ("Message index: {0}" -f $messageIndexPath)
Write-Output ("Bad request list: {0}" -f $badRequestPath)
Write-Output ("Move results: {0}" -f $moveResultsPath)
