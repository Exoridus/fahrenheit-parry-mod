param(
    [string]$Root = ".workspace/discord",
    [string]$OutDir = ".workspace/analysis/discord-ocr",
    [string]$ApiBase = "http://10.0.20.40:1234/v1",
    [string]$ApiKey = "",
    [string]$TokenEnvVar = "LMSTUDIO_API_KEY",
    [string]$FastModel = "qwen3-vl-8b-instruct-mlx@4bit",
    [string]$LargeModel = "qwen3-vl-30b-a3b-instruct-mlx",
    [int]$FastMaxDimension = 640,
    [int]$LargeMaxDimension = 1400,
    [int]$MinBytes = 0,
    [int]$MinDimension = 0,
    [int]$FastAcceptFullTextConfidence = 95,
    [int]$FastAcceptNoTextConfidence = 95,
    [int]$BatchSize = 25,
    [int]$Limit = 0,
    [switch]$Resume,
    [switch]$Full
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

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

function Write-JsonLine {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Object
    )

    Add-Content -LiteralPath $Path -Value (($Object | ConvertTo-Json -Depth 20 -Compress) + [Environment]::NewLine) -Encoding utf8NoBOM
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

function Get-ImageInfo {
    param([Parameter(Mandatory)][string]$Path)

    $file = Get-Item -LiteralPath $Path
    $ext = $file.Extension.ToLowerInvariant()

    if ($ext -eq ".svg") {
        return [pscustomobject]@{
            Extension = $ext
            Bytes = $file.Length
            Width = 0
            Height = 0
        }
    }

    if ($ext -eq ".webp") {
        return [pscustomobject]@{
            Extension = $ext
            Bytes = $file.Length
            Width = 0
            Height = 0
        }
    }

    $img = [System.Drawing.Image]::FromFile($Path)
    try {
        return [pscustomobject]@{
            Extension = $ext
            Bytes = $file.Length
            Width = $img.Width
            Height = $img.Height
        }
    }
    finally {
        $img.Dispose()
    }
}

function Get-LocalFilter {
    param(
        [Parameter(Mandatory)]$ImageInfo,
        [int]$MinBytes = 0,
        [int]$MinDimension = 0
    )

    if ($ImageInfo.Extension -eq ".svg") {
        return "skip_svg"
    }

    if ($ImageInfo.Width -gt 0 -and $ImageInfo.Height -gt 0) {
        if ($ImageInfo.Width -le 96 -and $ImageInfo.Height -le 96) {
            return "skip_tiny"
        }

        if ($ImageInfo.Width -le 256 -and $ImageInfo.Height -le 256 -and $ImageInfo.Bytes -lt 40000) {
            return "skip_small"
        }
    }

    if ($MinBytes -gt 0 -and $MinDimension -gt 0 -and $ImageInfo.Bytes -lt $MinBytes -and [Math]::Max($ImageInfo.Width, $ImageInfo.Height) -lt $MinDimension) {
        return "skip_low_signal"
    }

    return "selected"
}

function Get-JsonPropertyValue {
    param(
        $Object,
        [Parameter(Mandatory)][string]$PropertyName,
        $Default = $null
    )

    if ($null -eq $Object) {
        return $Default
    }

    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        return $Default
    }

    return $property.Value
}

function Get-JsonArrayValue {
    param(
        $Object,
        [Parameter(Mandatory)][string]$PropertyName
    )

    $value = Get-JsonPropertyValue -Object $Object -PropertyName $PropertyName
    if ($null -eq $value) {
        return @()
    }

    return @($value)
}

function Build-Manifest {
    param(
        [Parameter(Mandatory)][string]$Root,
        [int]$MinBytes = 0,
        [int]$MinDimension = 0
    )

    $exports = Get-ChildItem -LiteralPath $Root -Recurse -File -Filter *.json |
        Where-Object {
            $_.FullName -notmatch '\\Media\\' -and
            $_.FullName -notmatch '_Files' -and
            $_.Name -ne 'config.local.json'
        }

    $refs = @{}
    $refMeta = @{}

    foreach ($export in $exports) {
        try {
            $json = Get-Content -LiteralPath $export.FullName -Raw | ConvertFrom-Json
            $guild = Get-JsonPropertyValue -Object $json -PropertyName "guild"
            $channel = Get-JsonPropertyValue -Object $json -PropertyName "channel"
            $guildId = Get-JsonStringValue -Object $guild -PropertyName "id"
            $guildName = Get-JsonStringValue -Object $guild -PropertyName "name"
            $channelId = Get-JsonStringValue -Object $channel -PropertyName "id"
            $channelName = Get-JsonStringValue -Object $channel -PropertyName "name"
            $exportRelPath = [System.IO.Path]::GetRelativePath($Root, $export.FullName)

            foreach ($message in (Get-JsonArrayValue -Object $json -PropertyName "messages")) {
                $messageId = Get-JsonStringValue -Object $message -PropertyName "id"
                $messageTimestamp = Get-JsonStringValue -Object $message -PropertyName "timestamp"
                $author = Get-JsonPropertyValue -Object $message -PropertyName "author"
                $authorId = Get-JsonStringValue -Object $author -PropertyName "id"
                $authorName = Get-JsonStringValue -Object $author -PropertyName "name"

                $attachmentIndex = 0
                foreach ($attachment in (Get-JsonArrayValue -Object $message -PropertyName "attachments")) {
                    $path = [string]$attachment.url
                    if (-not [string]::IsNullOrWhiteSpace($path) -and $path -match '[\\/]+Media[\\/]' -and $path -match '\.(png|jpe?g|gif|webp|bmp|tiff|svg)$' -and (Test-Path -LiteralPath $path)) {
                        if (-not $refs.ContainsKey($path)) {
                            $refs[$path] = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
                        }

                        [void]$refs[$path].Add("attachments.url")

                        if (-not $refMeta.ContainsKey($path)) {
                            $refMeta[$path] = [System.Collections.Generic.List[object]]::new()
                        }

                        $refMeta[$path].Add([pscustomobject]@{
                            SourceKind = "attachments.url"
                            ExportFile = $exportRelPath
                            GuildId = $guildId
                            GuildName = $guildName
                            ChannelId = $channelId
                            ChannelName = $channelName
                            MessageId = $messageId
                            MessageTimestamp = $messageTimestamp
                            AuthorId = $authorId
                            AuthorName = $authorName
                            AttachmentIndex = $attachmentIndex
                            EmbedIndex = -1
                            EmbedImageIndex = -1
                        })
                    }
                    $attachmentIndex++
                }

                $embedIndex = 0
                foreach ($embed in (Get-JsonArrayValue -Object $message -PropertyName "embeds")) {
                    $embedImage = Get-JsonPropertyValue -Object $embed -PropertyName "image"
                    if ($null -ne $embedImage) {
                        $path = [string](Get-JsonPropertyValue -Object $embedImage -PropertyName "url")
                        if (-not [string]::IsNullOrWhiteSpace($path) -and $path -match '[\\/]+Media[\\/]' -and $path -match '\.(png|jpe?g|gif|webp|bmp|tiff|svg)$' -and (Test-Path -LiteralPath $path)) {
                            if (-not $refs.ContainsKey($path)) {
                                $refs[$path] = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
                            }

                            [void]$refs[$path].Add("embeds.image.url")

                            if (-not $refMeta.ContainsKey($path)) {
                                $refMeta[$path] = [System.Collections.Generic.List[object]]::new()
                            }

                            $refMeta[$path].Add([pscustomobject]@{
                                SourceKind = "embeds.image.url"
                                ExportFile = $exportRelPath
                                GuildId = $guildId
                                GuildName = $guildName
                                ChannelId = $channelId
                                ChannelName = $channelName
                                MessageId = $messageId
                                MessageTimestamp = $messageTimestamp
                                AuthorId = $authorId
                                AuthorName = $authorName
                                AttachmentIndex = -1
                                EmbedIndex = $embedIndex
                                EmbedImageIndex = -1
                            })
                        }
                    }
                    $embedImageIndex = 0
                    foreach ($image in (Get-JsonArrayValue -Object $embed -PropertyName "images")) {
                        $path = [string](Get-JsonPropertyValue -Object $image -PropertyName "url")
                        if (-not [string]::IsNullOrWhiteSpace($path) -and $path -match '[\\/]+Media[\\/]' -and $path -match '\.(png|jpe?g|gif|webp|bmp|tiff|svg)$' -and (Test-Path -LiteralPath $path)) {
                            if (-not $refs.ContainsKey($path)) {
                                $refs[$path] = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
                            }

                            [void]$refs[$path].Add("embeds.images.url")

                            if (-not $refMeta.ContainsKey($path)) {
                                $refMeta[$path] = [System.Collections.Generic.List[object]]::new()
                            }

                            $refMeta[$path].Add([pscustomobject]@{
                                SourceKind = "embeds.images.url"
                                ExportFile = $exportRelPath
                                GuildId = $guildId
                                GuildName = $guildName
                                ChannelId = $channelId
                                ChannelName = $channelName
                                MessageId = $messageId
                                MessageTimestamp = $messageTimestamp
                                AuthorId = $authorId
                                AuthorName = $authorName
                                AttachmentIndex = -1
                                EmbedIndex = $embedIndex
                                EmbedImageIndex = $embedImageIndex
                            })
                        }
                        $embedImageIndex++
                    }
                    $embedIndex++
                }
            }
        }
        catch {
        }
    }

    $manifest = [System.Collections.Generic.List[object]]::new()
    foreach ($path in ($refs.Keys | Sort-Object)) {
        try {
            $info = Get-ImageInfo -Path $path
            $filter = Get-LocalFilter -ImageInfo $info -MinBytes $MinBytes -MinDimension $MinDimension
            $manifest.Add([pscustomobject]@{
                Path = $path
                Extension = $info.Extension
                Bytes = $info.Bytes
                Width = $info.Width
                Height = $info.Height
                SourceKinds = @($refs[$path] | Sort-Object)
                SourceRefCount = @($refMeta[$path]).Count
                SourceRefs = @($refMeta[$path] | Sort-Object ExportFile,MessageTimestamp,MessageId,SourceKind)
                LocalFilter = $filter
                Selected = ($filter -eq "selected")
            })
        }
        catch {
            $manifest.Add([pscustomobject]@{
                Path = $path
                Extension = [System.IO.Path]::GetExtension($path).ToLowerInvariant()
                Bytes = (Get-Item -LiteralPath $path).Length
                Width = 0
                Height = 0
                SourceKinds = @($refs[$path] | Sort-Object)
                SourceRefCount = @($refMeta[$path]).Count
                SourceRefs = @($refMeta[$path] | Sort-Object ExportFile,MessageTimestamp,MessageId,SourceKind)
                LocalFilter = "selected"
                Selected = $true
            })
        }
    }

    return @($manifest)
}

function Parse-JsonContent {
    param([string]$Content)

    if ([string]::IsNullOrWhiteSpace($Content)) {
        return $null
    }

    $trimmed = $Content.Trim()
    $candidates = [System.Collections.Generic.List[string]]::new()
    $candidates.Add($trimmed)

    if ($trimmed -match '(?s)```(?:json)?\s*(\{.*?\})\s*```') {
        $candidates.Add($matches[1])
    }

    if ($trimmed -match '(?s)(\{.*\})') {
        $candidates.Add($matches[1])
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        try {
            return ($candidate | ConvertFrom-Json)
        }
        catch {
        }
    }

    return $null
}

function Get-RestApiBase {
    param([Parameter(Mandatory)][string]$ApiBase)

    if ($ApiBase -match '^(https?://[^/]+)(?:/.*)?$') {
        return $matches[1] + "/api/v1"
    }

    throw "Unable to derive REST API base from $ApiBase"
}

function Get-ExceptionResponseBody {
    param($Exception)

    if ($null -eq $Exception -or $null -eq $Exception.Response) {
        return $null
    }

    $response = $Exception.Response

    try {
        if ($response.PSObject.Methods.Name -contains "GetResponseStream") {
            $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
            try {
                return $reader.ReadToEnd()
            }
            finally {
                $reader.Close()
            }
        }

        if ($null -ne $response.Content) {
            return $response.Content.ReadAsStringAsync().Result
        }
    }
    catch {
    }

    return $null
}

function Ensure-ModelLoaded {
    param(
        [Parameter(Mandatory)][string]$ApiBase,
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][string]$Model
    )

    $restApiBase = Get-RestApiBase -ApiBase $ApiBase
    $body = @{
        model = $Model
        echo_load_config = $false
    } | ConvertTo-Json -Depth 6

    $response = Invoke-WebRequest -Uri ($restApiBase + "/models/load") `
        -Method Post `
        -Headers @{
            Authorization = "Bearer $Token"
            "Content-Type" = "application/json"
        } `
        -Body $body `
        -TimeoutSec 600 `
        -SkipHttpErrorCheck

    if ([int]$response.StatusCode -lt 200 -or [int]$response.StatusCode -ge 300) {
        throw ("Model load failed for {0}: HTTP {1} :: {2}" -f $Model, [int]$response.StatusCode, $response.Content)
    }
}

function Unload-Model {
    param(
        [Parameter(Mandatory)][string]$ApiBase,
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][string]$Model
    )

    $restApiBase = Get-RestApiBase -ApiBase $ApiBase
    $body = @{
        instance_id = $Model
    } | ConvertTo-Json -Depth 4

    $response = Invoke-WebRequest -Uri ($restApiBase + "/models/unload") `
        -Method Post `
        -Headers @{
            Authorization = "Bearer $Token"
            "Content-Type" = "application/json"
        } `
        -Body $body `
        -TimeoutSec 120 `
        -SkipHttpErrorCheck

    if ([int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 300) {
        return
    }

    if ($response.Content -match 'not loaded') {
        return
    }

    throw ("Model unload failed for {0}: HTTP {1} :: {2}" -f $Model, [int]$response.StatusCode, $response.Content)
}

function Get-JsonStringValue {
    param(
        $Object,
        [Parameter(Mandatory)][string]$PropertyName,
        [string]$Default = ""
    )

    if ($null -eq $Object) {
        return $Default
    }

    $value = Get-JsonPropertyValue -Object $Object -PropertyName $PropertyName
    if ($null -eq $value) {
        return $Default
    }

    return [string]$value
}

function Get-JsonIntValue {
    param(
        $Object,
        [Parameter(Mandatory)][string]$PropertyName,
        [int]$Default = 0
    )

    if ($null -eq $Object) {
        return $Default
    }

    $value = Get-JsonPropertyValue -Object $Object -PropertyName $PropertyName
    if ($null -eq $value) {
        return $Default
    }

    try {
        return [int]$value
    }
    catch {
        return $Default
    }
}

function Get-JsonBoolValue {
    param(
        $Object,
        [Parameter(Mandatory)][string]$PropertyName,
        [bool]$Default = $false
    )

    if ($null -eq $Object) {
        return $Default
    }

    $value = Get-JsonPropertyValue -Object $Object -PropertyName $PropertyName
    if ($null -eq $value) {
        return $Default
    }

    try {
        return [bool]$value
    }
    catch {
        return $Default
    }
}

function Should-EscalateToPass2 {
    param($Pass1Row)

    if ($null -eq $Pass1Row) {
        return $true
    }

    if ($Pass1Row.Classification -eq "error") {
        return $true
    }

    $notes = [string]$Pass1Row.Notes
    $text = [string]$Pass1Row.Text
    $confidence = [int]$Pass1Row.Confidence

    if ($Pass1Row.Classification -eq "no_text" -and $confidence -ge $FastAcceptNoTextConfidence) {
        return $false
    }

    if (
        $Pass1Row.Classification -eq "full_text" -and
        $confidence -ge $FastAcceptFullTextConfidence -and
        -not [string]::IsNullOrWhiteSpace($text) -and
        [string]::IsNullOrWhiteSpace($notes)
    ) {
        return $false
    }

    return $true
}

function Get-PreparedImage {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$MaxDimension,
        [switch]$AlwaysConvertToJpeg
    )

    $ext = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
    if ($ext -eq ".webp") {
        return [pscustomobject]@{
            TempPath = $null
            PayloadPath = $Path
            MimeType = "image/webp"
        }
    }

    if ($ext -eq ".svg") {
        throw "SVG is not supported in the OCR pipeline"
    }

    $img = [System.Drawing.Image]::FromFile($Path)
    try {
        $scale = [Math]::Min($MaxDimension / $img.Width, $MaxDimension / $img.Height)
        if ($scale -gt 1) {
            $scale = 1
        }

        $width = [int][Math]::Round($img.Width * $scale)
        $height = [int][Math]::Round($img.Height * $scale)

        if (-not $AlwaysConvertToJpeg -and $width -eq $img.Width -and $height -eq $img.Height) {
            return [pscustomobject]@{
                TempPath = $null
                PayloadPath = $Path
                MimeType = if ($ext -eq ".png") { "image/png" } elseif ($ext -eq ".gif") { "image/gif" } else { "image/jpeg" }
            }
        }

        $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ("ocr_" + [System.Guid]::NewGuid().ToString("N") + ".jpg")
        $bmp = New-Object System.Drawing.Bitmap $width, $height
        $graphics = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($img, 0, 0, $width, $height)
            $bmp.Save($tempPath, [System.Drawing.Imaging.ImageFormat]::Jpeg)
        }
        finally {
            $graphics.Dispose()
            $bmp.Dispose()
        }

        return [pscustomobject]@{
            TempPath = $tempPath
            PayloadPath = $tempPath
            MimeType = "image/jpeg"
        }
    }
    finally {
        $img.Dispose()
    }
}

function Test-HasOcrSidecarText {
    param([Parameter(Mandatory)][string]$ImagePath)

    $sidecarPath = $ImagePath + ".ocr.txt"
    if (-not (Test-Path -LiteralPath $sidecarPath)) {
        return $false
    }

    try {
        $text = Get-Content -LiteralPath $sidecarPath -Raw
        return -not [string]::IsNullOrWhiteSpace($text)
    }
    catch {
        return $false
    }
}

function Invoke-VisionPass {
    param(
        [Parameter(Mandatory)][string]$ApiBase,
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][string]$Model,
        [Parameter(Mandatory)][string]$ImagePath,
        [Parameter(Mandatory)][int]$MaxDimension,
        [Parameter(Mandatory)][string]$Prompt,
        [int]$MaxTokens = 250,
        [int]$TimeoutSec = 300,
        [int]$MaxAttempts = 3,
        [switch]$AlwaysConvertToJpeg
    )

    $prepared = Get-PreparedImage -Path $ImagePath -MaxDimension $MaxDimension -AlwaysConvertToJpeg:$AlwaysConvertToJpeg
    try {
        $bytes = [System.IO.File]::ReadAllBytes($prepared.PayloadPath)
        $base64 = [Convert]::ToBase64String($bytes)
        $body = @{
            model = $Model
            temperature = 0
            max_tokens = $MaxTokens
            messages = @(
                @{
                    role = "user"
                    content = @(
                        @{
                            type = "text"
                            text = $Prompt
                        },
                        @{
                            type = "image_url"
                            image_url = @{
                                url = ("data:{0};base64,{1}" -f $prepared.MimeType, $base64)
                            }
                        }
                    )
                }
            )
        } | ConvertTo-Json -Depth 12

        $response = $null
        $sw = [System.Diagnostics.Stopwatch]::new()
        $lastMessage = $null
        $lastBody = $null
        for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
            try {
                $sw.Restart()
                $response = Invoke-RestMethod -Uri ($ApiBase.TrimEnd("/") + "/chat/completions") `
                    -Method Post `
                    -Headers @{
                        Authorization = "Bearer $Token"
                        "Content-Type" = "application/json"
                    } `
                    -Body $body `
                    -TimeoutSec $TimeoutSec
                $sw.Stop()
                break
            }
            catch {
                $sw.Stop()
                $lastMessage = $_.Exception.Message
                $lastBody = Get-ExceptionResponseBody -Exception $_.Exception
                $combinedError = (($lastMessage ?? "") + " " + ($lastBody ?? ""))

                if ($combinedError -match 'has been unloaded|not started loading') {
                    try {
                        Unload-Model -ApiBase $ApiBase -Token $Token -Model $Model
                    }
                    catch {
                    }

                    try {
                        Ensure-ModelLoaded -ApiBase $ApiBase -Token $Token -Model $Model
                    }
                    catch {
                    }
                }
                elseif ($combinedError -match 'model has crashed|channel error|segmentation fault') {
                    try {
                        Unload-Model -ApiBase $ApiBase -Token $Token -Model $Model
                    }
                    catch {
                    }

                    try {
                        Ensure-ModelLoaded -ApiBase $ApiBase -Token $Token -Model $Model
                    }
                    catch {
                    }

                    if (-not [string]::IsNullOrWhiteSpace($lastBody)) {
                        throw ("{0} :: {1}" -f $lastMessage, $lastBody)
                    }

                    throw
                }

                if ($attempt -ge $MaxAttempts) {
                    if (-not [string]::IsNullOrWhiteSpace($lastBody)) {
                        throw ("{0} :: {1}" -f $lastMessage, $lastBody)
                    }

                    throw
                }

                Start-Sleep -Seconds ([Math]::Min(5, $attempt * 2))
            }
        }

        $content = Get-JsonStringValue -Object $response.choices[0].message -PropertyName "content"
        $finishReason = Get-JsonStringValue -Object $response.choices[0] -PropertyName "finish_reason"
        $parsed = if ($finishReason -eq "length") { $null } else { Parse-JsonContent -Content $content }

        return [pscustomobject]@{
            RawContent = $content
            Parsed = $parsed
            Seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 2)
            FinishReason = $finishReason
            PromptTokens = $response.usage.prompt_tokens
            CompletionTokens = $response.usage.completion_tokens
            TotalTokens = $response.usage.total_tokens
        }
    }
    finally {
        if ($null -ne $prepared.TempPath -and (Test-Path -LiteralPath $prepared.TempPath)) {
            Remove-Item -LiteralPath $prepared.TempPath -Force
        }
    }
}

function Save-Summary {
    param(
        [Parameter(Mandatory)][string]$OutPath,
        [Parameter(Mandatory)]$Manifest,
        $Pass1Rows,
        $Pass2Rows
    )

    $manifestRows = @($Manifest | Where-Object { $null -ne $_ })
    $pass1Clean = @($Pass1Rows | Where-Object { $null -ne $_ })
    $pass2Clean = @($Pass2Rows | Where-Object { $null -ne $_ })
    $selectedCount = @($manifestRows | Where-Object Selected).Count
    $pass1ByClass = @{}
    foreach ($group in ($pass1Clean | Group-Object Classification | Sort-Object Name)) {
        $pass1ByClass[$group.Name] = @($group.Group).Count
    }

    $pass2Queued = @($pass1Clean | Where-Object { Should-EscalateToPass2 -Pass1Row $_ }).Count
    $acceptedAfterFast = @($pass1Clean).Count - $pass2Queued

    $summary = [pscustomobject]@{
        UpdatedAt = (Get-Date).ToString("s")
        ManifestTotal = @($manifestRows).Count
        ManifestSelected = $selectedCount
        Pass1Processed = @($pass1Clean).Count
        Pass1ByClassification = $pass1ByClass
        AcceptedAfterFast = $acceptedAfterFast
        Pass2Queued = $pass2Queued
        Pass2Processed = @($pass2Clean).Count
    }

    Set-Content -LiteralPath $OutPath -Value (($summary | ConvertTo-Json -Depth 20) + [Environment]::NewLine) -Encoding utf8NoBOM
}

$rootPath = Get-AbsolutePath -Path $Root
$outPath = Get-AbsolutePath -Path $OutDir
Ensure-Directory -Path $outPath

$token = $ApiKey.Trim()
if ([string]::IsNullOrWhiteSpace($token)) {
    $token = [Environment]::GetEnvironmentVariable($TokenEnvVar)
}

if ([string]::IsNullOrWhiteSpace($token)) {
    throw "Missing API key. Pass -ApiKey or set environment variable $TokenEnvVar."
}

$manifestPath = Join-Path $outPath "manifest.json"
$pass1Path = Join-Path $outPath "pass1.jsonl"
$pass2Path = Join-Path $outPath "pass2.jsonl"
$summaryPath = Join-Path $outPath "summary.json"
$logPath = Join-Path $outPath "run.log"

function Write-Log {
    param([Parameter(Mandatory)][string]$Message)
    $line = "[{0}] {1}" -f (Get-Date).ToString("s"), $Message
    Add-Content -LiteralPath $logPath -Value ($line + [Environment]::NewLine) -Encoding utf8NoBOM
}

if ($Resume -and (Test-Path -LiteralPath $manifestPath)) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Write-Log "Loaded existing manifest from $manifestPath"
}
else {
    Write-Log "Building manifest..."
    $manifest = Build-Manifest -Root $rootPath -MinBytes $MinBytes -MinDimension $MinDimension
    Set-Content -LiteralPath $manifestPath -Value (($manifest | ConvertTo-Json -Depth 20) + [Environment]::NewLine) -Encoding utf8NoBOM
    Write-Log ("Manifest built with {0} entries, {1} selected." -f @($manifest).Count, @($manifest | Where-Object Selected).Count)
}

$pass1Existing = Read-JsonLines -Path $pass1Path
$pass2Existing = Read-JsonLines -Path $pass2Path
$pass1Done = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$pass2Done = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($row in $pass1Existing) { [void]$pass1Done.Add($row.Path) }
foreach ($row in $pass2Existing) { [void]$pass2Done.Add($row.Path) }

$fastPrompt = "Return only minified JSON with keys classification,text,confidence,notes. classification must be one of no_text, full_text, needs_review. Ignore tiny incidental text on clothing, logos, decorative art, watermarks, and background clutter unless it is the main foreground subject. Use no_text if there is no useful foreground text worth OCR. Use full_text only if all useful visible foreground text is clearly readable and exactly transcribed. Otherwise use needs_review. confidence must be an integer 0-100."
$largePrompt = "Return only minified JSON with keys complete,text,confidence,notes. Do exact OCR of all useful foreground text in this image. Ignore decorative or incidental background text unless it is clearly part of the main content. complete must be true only if all useful visible text is transcribed exactly. confidence must be an integer 0-100. Preserve line breaks when useful."

$selectedItems = @($manifest | Where-Object Selected)
if (-not $Full) {
    $selectedItems = @($selectedItems | Where-Object { -not (Test-HasOcrSidecarText -ImagePath $_.Path) })
}

if ($Limit -gt 0) {
    $selectedItems = @($selectedItems | Select-Object -First $Limit)
}

if (@($selectedItems).Count -eq 0) {
    Write-Log "No OCR candidates selected after sidecar and limit filters."
    Save-Summary -OutPath $summaryPath -Manifest $manifest -Pass1Rows $pass1Existing -Pass2Rows $pass2Existing
    [pscustomobject]@{
        ManifestPath = $manifestPath
        Pass1Path = $pass1Path
        Pass2Path = $pass2Path
        SummaryPath = $summaryPath
        LogPath = $logPath
        Selected = 0
        Pass1Done = @($pass1Existing).Count
        Pass2Done = @($pass2Existing).Count
    } | ConvertTo-Json -Depth 10
    return
}

Write-Log ("Unloading large model before pass 1: {0}" -f $LargeModel)
try {
    Unload-Model -ApiBase $ApiBase -Token $token -Model $LargeModel
}
catch {
    Write-Log ("Large model unload warning: {0}" -f $_.Exception.Message)
}
Write-Log ("Ensuring fast model is loaded: {0}" -f $FastModel)
Ensure-ModelLoaded -ApiBase $ApiBase -Token $token -Model $FastModel
Write-Log ("Starting pass 1 for up to {0} items." -f @($selectedItems).Count)
$pass1Counter = 0
foreach ($item in $selectedItems) {
    if ($pass1Done.Contains($item.Path)) {
        continue
    }

    $record = [ordered]@{
        Path = $item.Path
        Model = $FastModel
        Pass = 1
        Seconds = 0
        Classification = "error"
        Text = ""
        Confidence = 0
        Notes = ""
        RawContent = ""
    }

    try {
        $result = Invoke-VisionPass -ApiBase $ApiBase -Token $token -Model $FastModel -ImagePath $item.Path -MaxDimension $FastMaxDimension -Prompt $fastPrompt -MaxTokens 220 -TimeoutSec 240 -AlwaysConvertToJpeg
        $record.Seconds = $result.Seconds
        $record.RawContent = $result.RawContent

        if ($null -ne $result.Parsed) {
            $record.Classification = Get-JsonStringValue -Object $result.Parsed -PropertyName "classification" -Default "needs_review"
            $record.Text = Get-JsonStringValue -Object $result.Parsed -PropertyName "text"
            $record.Confidence = Get-JsonIntValue -Object $result.Parsed -PropertyName "confidence"
            $record.Notes = Get-JsonStringValue -Object $result.Parsed -PropertyName "notes"
        }
        else {
            $record.Classification = "needs_review"
            $record.Text = ""
            $record.Confidence = 0
            $record.Notes = if ([string]::IsNullOrWhiteSpace($result.FinishReason)) { "unparsed_json" } else { "finish_reason:" + $result.FinishReason }
        }
    }
    catch {
        $record.Notes = $_.Exception.Message
    }

    Write-JsonLine -Path $pass1Path -Object ([pscustomobject]$record)
    [void]$pass1Done.Add($item.Path)
    $pass1Counter++

    if (($pass1Counter % $BatchSize) -eq 0) {
        $pass1Existing = Read-JsonLines -Path $pass1Path
        Save-Summary -OutPath $summaryPath -Manifest $manifest -Pass1Rows $pass1Existing -Pass2Rows $pass2Existing
        Write-Log ("Pass 1 progress: {0} new items processed." -f $pass1Counter)
    }
}

$pass1Existing = Read-JsonLines -Path $pass1Path
Save-Summary -OutPath $summaryPath -Manifest $manifest -Pass1Rows $pass1Existing -Pass2Rows $pass2Existing
Write-Log "Pass 1 completed."

$pass2Candidates = @($pass1Existing | Where-Object { (Should-EscalateToPass2 -Pass1Row $_) -and -not $pass2Done.Contains($_.Path) })
if ($Limit -gt 0) {
    $pass2Candidates = @($pass2Candidates | Select-Object -First $Limit)
}

Write-Log ("Ensuring large model is loaded: {0}" -f $LargeModel)
Write-Log ("Unloading fast model before pass 2: {0}" -f $FastModel)
try {
    Unload-Model -ApiBase $ApiBase -Token $token -Model $FastModel
}
catch {
    Write-Log ("Fast model unload warning: {0}" -f $_.Exception.Message)
}
Ensure-ModelLoaded -ApiBase $ApiBase -Token $token -Model $LargeModel
Write-Log ("Starting pass 2 for {0} items." -f @($pass2Candidates).Count)
$pass2Counter = 0
foreach ($item in $pass2Candidates) {
    $record = [ordered]@{
        Path = $item.Path
        Model = $LargeModel
        Pass = 2
        Seconds = 0
        Complete = $false
        Text = ""
        Confidence = 0
        Notes = ""
        RawContent = ""
    }

    try {
        $result = Invoke-VisionPass -ApiBase $ApiBase -Token $token -Model $LargeModel -ImagePath $item.Path -MaxDimension $LargeMaxDimension -Prompt $largePrompt -MaxTokens 700 -TimeoutSec 420 -AlwaysConvertToJpeg
        $record.Seconds = $result.Seconds
        $record.RawContent = $result.RawContent

        if ($null -ne $result.Parsed) {
            $record.Complete = Get-JsonBoolValue -Object $result.Parsed -PropertyName "complete"
            $record.Text = Get-JsonStringValue -Object $result.Parsed -PropertyName "text"
            $record.Confidence = Get-JsonIntValue -Object $result.Parsed -PropertyName "confidence"
            $record.Notes = Get-JsonStringValue -Object $result.Parsed -PropertyName "notes"
        }
        else {
            $record.Notes = if ([string]::IsNullOrWhiteSpace($result.FinishReason)) { "unparsed_json" } else { "finish_reason:" + $result.FinishReason }
        }
    }
    catch {
        $record.Notes = $_.Exception.Message
    }

    Write-JsonLine -Path $pass2Path -Object ([pscustomobject]$record)
    [void]$pass2Done.Add($item.Path)
    $pass2Counter++

    if (($pass2Counter % $BatchSize) -eq 0) {
        $pass2Existing = Read-JsonLines -Path $pass2Path
        Save-Summary -OutPath $summaryPath -Manifest $manifest -Pass1Rows $pass1Existing -Pass2Rows $pass2Existing
        Write-Log ("Pass 2 progress: {0} new items processed." -f $pass2Counter)
    }
}

$pass2Existing = Read-JsonLines -Path $pass2Path
Save-Summary -OutPath $summaryPath -Manifest $manifest -Pass1Rows $pass1Existing -Pass2Rows $pass2Existing
Write-Log "Pass 2 completed."

[pscustomobject]@{
    ManifestPath = $manifestPath
    Pass1Path = $pass1Path
    Pass2Path = $pass2Path
    SummaryPath = $summaryPath
    LogPath = $logPath
    Selected = @($selectedItems).Count
    Pass1Done = @($pass1Existing).Count
    Pass2Done = @($pass2Existing).Count
} | ConvertTo-Json -Depth 10
