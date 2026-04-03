param(
    [string]$GuildRoot,
    [string]$OcrOutDir,
    [int]$MinConfidence = 85,
    [switch]$IncludeNonCode,
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

function Get-MarkdownFence {
    param([string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return '```'
    }

    if ($Text -match '````') {
        return '`````'
    }

    if ($Text -match '```') {
        return '````'
    }

    return '```'
}

function New-OcrSnippet {
    param(
        [Parameter(Mandatory)]$Row
    )

    $text = [string]$Row.OcrText
    $imageFile = [System.IO.Path]::GetFileName([string]$Row.ImagePath)
    $confidence = [int]$Row.OcrConfidence
    $fence = Get-MarkdownFence -Text $text

    $header = "[OCR extracted from image: {0} | confidence: {1}]" -f $imageFile, $confidence
    return @(
        $header
        ($fence + "text")
        $text
        $fence
    ) -join [Environment]::NewLine
}

if ([string]::IsNullOrWhiteSpace($GuildRoot)) {
    throw "GuildRoot is required."
}

if ([string]::IsNullOrWhiteSpace($OcrOutDir)) {
    throw "OcrOutDir is required."
}

$guildRootPath = Get-AbsolutePath -Path $GuildRoot
$ocrOutPath = Get-AbsolutePath -Path $OcrOutDir

if (-not (Test-Path -LiteralPath $guildRootPath)) {
    throw "GuildRoot does not exist: $guildRootPath"
}

if (-not (Test-Path -LiteralPath $ocrOutPath)) {
    throw "OcrOutDir does not exist: $ocrOutPath"
}

$linksPath = Join-Path $ocrOutPath "ocr_message_image_links.jsonl"
if (-not (Test-Path -LiteralPath $linksPath)) {
    throw "Missing OCR links file: $linksPath"
}

$allLinks = Read-JsonLines -Path $linksPath
$eligible = @(
    $allLinks | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.OcrText) -and
        ([string]$_.OcrClassification -in @("full_text", "partial_text")) -and
        [int]$_.OcrConfidence -ge $MinConfidence -and
        ($IncludeNonCode -or [bool]$_.OcrLooksLikeCode)
    }
)

$linksByMessage = @{}
foreach ($row in $eligible) {
    $key = [string]$row.MessageKey
    if (-not $linksByMessage.ContainsKey($key)) {
        $linksByMessage[$key] = [System.Collections.Generic.List[object]]::new()
    }
    $linksByMessage[$key].Add($row)
}

$exportFiles = @{}
foreach ($row in $eligible) {
    $exportFileRel = [string]$row.ExportFile
    if ([string]::IsNullOrWhiteSpace($exportFileRel)) {
        continue
    }

    if (-not $exportFiles.ContainsKey($exportFileRel)) {
        $exportFiles[$exportFileRel] = $true
    }
}

$backupRoot = Join-Path $ocrOutPath "backup-before-embed"
if (-not $DryRun) {
    Ensure-Directory -Path $backupRoot
}

$changes = [System.Collections.Generic.List[object]]::new()
$totalMessagesChanged = 0
$totalSnippetsInserted = 0
$totalAttachmentRefsRemoved = 0
$totalEmbedImageRefsRemoved = 0
$totalEmbedImagesRefsRemoved = 0
$fileCountChanged = 0

foreach ($exportFileRel in ($exportFiles.Keys | Sort-Object)) {
    $jsonPath = Join-Path $guildRootPath $exportFileRel
    if (-not (Test-Path -LiteralPath $jsonPath)) {
        $changes.Add([pscustomobject]@{
            ExportFile = $exportFileRel
            Status = "missing"
            MessagesChanged = 0
            SnippetsInserted = 0
            AttachmentRefsRemoved = 0
            EmbedImageRefsRemoved = 0
            EmbedImagesRefsRemoved = 0
        })
        continue
    }

    $json = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    $messages = @($json.messages)
    if ($messages.Count -eq 0) {
        $changes.Add([pscustomobject]@{
            ExportFile = $exportFileRel
            Status = "no_messages"
            MessagesChanged = 0
            SnippetsInserted = 0
            AttachmentRefsRemoved = 0
            EmbedImageRefsRemoved = 0
            EmbedImagesRefsRemoved = 0
        })
        continue
    }

    $fileMessagesChanged = 0
    $fileSnippetsInserted = 0
    $fileAttachmentRefsRemoved = 0
    $fileEmbedImageRefsRemoved = 0
    $fileEmbedImagesRefsRemoved = 0

    foreach ($message in $messages) {
        $messageId = [string]$message.id
        $channelId = [string]$json.channel.id
        $messageKey = "$exportFileRel|$channelId|$messageId"
        if (-not $linksByMessage.ContainsKey($messageKey)) {
            continue
        }

        $rowsRaw = @($linksByMessage[$messageKey])
        if ($rowsRaw.Count -eq 0) {
            continue
        }

        $rows = @(
            $rowsRaw |
            Sort-Object AttachmentIndex,EmbedIndex,EmbedImageIndex,ImagePath |
            Group-Object ImagePath,SourceKind |
            ForEach-Object { $_.Group[0] }
        )

        if ($rows.Count -eq 0) {
            continue
        }

        $snippets = [System.Collections.Generic.List[string]]::new()
        foreach ($row in $rows) {
            $snippets.Add((New-OcrSnippet -Row $row))
        }

        $appendBlock = @(
            "OCR extracted snippets:"
            ($snippets -join ([Environment]::NewLine + [Environment]::NewLine))
        ) -join [Environment]::NewLine

        $content = [string]$message.content
        if ([string]::IsNullOrWhiteSpace($content)) {
            $message.content = $appendBlock
        }
        else {
            $message.content = $content.TrimEnd() + [Environment]::NewLine + [Environment]::NewLine + $appendBlock
        }

        $sourceAttachmentPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $sourceEmbedImagePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $sourceEmbedImagesPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

        foreach ($row in $rows) {
            $p = [string]$row.ImagePath
            $k = [string]$row.SourceKind
            if ($k -eq "attachments.url") { [void]$sourceAttachmentPaths.Add($p) }
            elseif ($k -eq "embeds.image.url") { [void]$sourceEmbedImagePaths.Add($p) }
            elseif ($k -eq "embeds.images.url") { [void]$sourceEmbedImagesPaths.Add($p) }
        }

        $beforeAttachments = @($message.attachments).Count
        if ($beforeAttachments -gt 0 -and $sourceAttachmentPaths.Count -gt 0) {
            $message.attachments = @(
                @($message.attachments) | Where-Object {
                    $url = [string]$_.url
                    -not $sourceAttachmentPaths.Contains($url)
                }
            )
            $afterAttachments = @($message.attachments).Count
            $fileAttachmentRefsRemoved += ($beforeAttachments - $afterAttachments)
        }

        $embedImageRemoved = 0
        $embedImagesRemoved = 0
        if (@($message.embeds).Count -gt 0) {
            foreach ($embed in @($message.embeds)) {
                if ($sourceEmbedImagePaths.Count -gt 0) {
                    $embedImage = $embed.PSObject.Properties["image"]
                    if ($null -ne $embedImage -and $null -ne $embedImage.Value) {
                        $url = [string]$embedImage.Value.url
                        if ($sourceEmbedImagePaths.Contains($url)) {
                            $embedImage.Value = $null
                            $embedImageRemoved++
                        }
                    }
                }

                if ($sourceEmbedImagesPaths.Count -gt 0) {
                    $imagesProp = $embed.PSObject.Properties["images"]
                    if ($null -ne $imagesProp -and $null -ne $imagesProp.Value) {
                        $before = @($imagesProp.Value).Count
                        $imagesProp.Value = @(
                            @($imagesProp.Value) | Where-Object {
                                $url = [string]$_.url
                                -not $sourceEmbedImagesPaths.Contains($url)
                            }
                        )
                        $after = @($imagesProp.Value).Count
                        $embedImagesRemoved += ($before - $after)
                    }
                }
            }
        }
        $fileEmbedImageRefsRemoved += $embedImageRemoved
        $fileEmbedImagesRefsRemoved += $embedImagesRemoved

        $fileMessagesChanged++
        $fileSnippetsInserted += $rows.Count
    }

    $status = "unchanged"
    if ($fileMessagesChanged -gt 0) {
        $status = if ($DryRun) { "dryrun_would_update" } else { "updated" }
        if (-not $DryRun) {
            $backupPath = Join-Path $backupRoot ($exportFileRel -replace '[\\/]', '__')
            if (-not (Test-Path -LiteralPath $backupPath)) {
                Copy-Item -LiteralPath $jsonPath -Destination $backupPath -Force
            }

            $outJson = $json | ConvertTo-Json -Depth 100
            Set-Content -LiteralPath $jsonPath -Value ($outJson + [Environment]::NewLine) -Encoding utf8NoBOM
        }
        $fileCountChanged++
    }

    $changes.Add([pscustomobject]@{
        ExportFile = $exportFileRel
        Status = $status
        MessagesChanged = $fileMessagesChanged
        SnippetsInserted = $fileSnippetsInserted
        AttachmentRefsRemoved = $fileAttachmentRefsRemoved
        EmbedImageRefsRemoved = $fileEmbedImageRefsRemoved
        EmbedImagesRefsRemoved = $fileEmbedImagesRefsRemoved
    })

    $totalMessagesChanged += $fileMessagesChanged
    $totalSnippetsInserted += $fileSnippetsInserted
    $totalAttachmentRefsRemoved += $fileAttachmentRefsRemoved
    $totalEmbedImageRefsRemoved += $fileEmbedImageRefsRemoved
    $totalEmbedImagesRefsRemoved += $fileEmbedImagesRefsRemoved
}

$summary = [pscustomobject]@{
    UpdatedAt = (Get-Date).ToString("s")
    GuildRoot = $guildRootPath
    OcrOutDir = $ocrOutPath
    DryRun = [bool]$DryRun
    MinConfidence = $MinConfidence
    IncludeNonCode = [bool]$IncludeNonCode
    EligibleLinks = $eligible.Count
    FilesConsidered = $exportFiles.Count
    FilesChanged = $fileCountChanged
    MessagesChanged = $totalMessagesChanged
    SnippetsInserted = $totalSnippetsInserted
    AttachmentRefsRemoved = $totalAttachmentRefsRemoved
    EmbedImageRefsRemoved = $totalEmbedImageRefsRemoved
    EmbedImagesRefsRemoved = $totalEmbedImagesRefsRemoved
}

$summaryPath = Join-Path $ocrOutPath "embed.summary.json"
$changesPath = Join-Path $ocrOutPath "embed.file_changes.jsonl"

Set-Content -LiteralPath $summaryPath -Value (($summary | ConvertTo-Json -Depth 20) + [Environment]::NewLine) -Encoding utf8NoBOM
if (Test-Path -LiteralPath $changesPath) {
    Remove-Item -LiteralPath $changesPath -Force
}

foreach ($change in $changes) {
    Add-Content -LiteralPath $changesPath -Value (($change | ConvertTo-Json -Depth 20 -Compress) + [Environment]::NewLine) -Encoding utf8NoBOM
}

Write-Output ("Embedding complete. Summary: {0}" -f $summaryPath)
Write-Output ("Per-file changes: {0}" -f $changesPath)
if (-not $DryRun) {
    Write-Output ("Backups: {0}" -f $backupRoot)
}
