param(
    [string]$OutputDir = "mappings/research",
    [string]$DiscordCompactCsv = ".workspace/analysis/discord_messages_compact.csv",
    [string]$FahrenheitCallMap = ".workspace/fahrenheit/core/fh/FFX/call.g.cs",
    [string]$LocalOffsetMap = "src/data/external_memory_offset_map.cs"
)

$ErrorActionPreference = "Stop"
$script:RepoRoot = [System.IO.Path]::GetFullPath((Get-Location).Path)

function Ensure-Dir([string]$path) {
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $path | Out-Null
    }
}

function Normalize-SourcePath([string]$sourcePath) {
    if ([string]::IsNullOrWhiteSpace($sourcePath)) { return $sourcePath }

    $candidate = $sourcePath.Trim().Trim('"')
    try {
        if ([System.IO.Path]::IsPathRooted($candidate)) {
            $full = [System.IO.Path]::GetFullPath($candidate)
            if ($full.StartsWith($script:RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                $rel = $full.Substring($script:RepoRoot.Length).TrimStart('\', '/')
                if (-not [string]::IsNullOrWhiteSpace($rel)) {
                    return ($rel -replace '\\','/')
                }
            }
            return ($full -replace '\\','/')
        }
    }
    catch {
        # ignore and fall back
    }

    return ($candidate -replace '\\','/')
}

function Parse-HexValue([string]$hexText) {
    try {
        return [uint64]::Parse($hexText, [System.Globalization.NumberStyles]::HexNumber)
    }
    catch {
        return $null
    }
}

function Normalize-Token([string]$token) {
    if ([string]::IsNullOrWhiteSpace($token)) { return $token }
    $trimmed = $token.Trim()
    if ($trimmed -match '^(?i)0x([0-9a-f]{4,10})$') {
        return ("0x{0}" -f $Matches[1].ToUpperInvariant())
    }
    if ($trimmed -match '^(?i)FUN_([0-9a-f]{6,10})$') {
        return ("FUN_{0}" -f $Matches[1].ToUpperInvariant())
    }
    if ($trimmed -match '^(?i)FFX\.exe\s*\+\s*0x([0-9a-f]{4,10})$') {
        return ("FFX.exe + 0x{0}" -f $Matches[1].ToUpperInvariant())
    }
    return $trimmed
}

function Normalize-Hex([uint64]$value) {
    return ("{0:X}" -f $value)
}

function Get-AddressCandidates([string]$token) {
    $candidates = New-Object System.Collections.Generic.HashSet[string]
    $base = 0x400000
    $hex = $null

    if ($token -match '^FUN_([0-9A-Fa-f]{6,10})$') {
        $hex = $Matches[1]
    }
    elseif ($token -match '^0x([0-9A-Fa-f]{4,10})$') {
        $hex = $Matches[1]
    }
    elseif ($token -match '^FFX\.exe\s*\+\s*0x([0-9A-Fa-f]{4,10})$') {
        $hex = $Matches[1]
    }

    if ($null -eq $hex) {
        return @()
    }

    $value = Parse-HexValue $hex
    if ($null -eq $value) {
        return @()
    }

    [void]$candidates.Add((Normalize-Hex $value))
    if ($value -gt $base) {
        [void]$candidates.Add((Normalize-Hex ($value - $base)))
    }
    [void]$candidates.Add((Normalize-Hex ($value + $base)))

    return @($candidates)
}

function Split-TokenValues([string]$text) {
    $regex = [regex]'(?i)\bFUN_[0-9A-F]{6,10}\b|FFX\.exe\s*\+\s*0x[0-9A-F]{4,10}\b|0x[0-9A-F]{4,10}\b'
    return $regex.Matches($text) | ForEach-Object { $_.Value.Trim() }
}

function Extract-SymbolHints([string]$text) {
    $regex = [regex]'\b(?:FUN_[0-9A-Fa-f]{6,10}|__addr_[A-Za-z0-9_]+|Ms|TO|Atel|Sg|Dmg|Btl|Chr|ei|Fh)[A-Za-z0-9_]*\b'
    $set = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::Ordinal)
    foreach ($m in $regex.Matches($text)) {
        [void]$set.Add($m.Value)
    }
    return @($set)
}

function Parse-TimestampOrNull([string]$timestampText) {
    if ([string]::IsNullOrWhiteSpace($timestampText)) { return $null }
    $formats = @(
        "MM/dd/yyyy HH:mm:ss",
        "M/d/yyyy H:mm:ss",
        "dd.MM.yyyy HH:mm:ss",
        "d.M.yyyy H:mm:ss"
    )
    $cultures = @(
        [System.Globalization.CultureInfo]::GetCultureInfo("en-US"),
        [System.Globalization.CultureInfo]::GetCultureInfo("de-DE"),
        [System.Globalization.CultureInfo]::InvariantCulture
    )
    foreach ($culture in $cultures) {
        try {
            return [datetime]::ParseExact($timestampText, $formats, $culture, [System.Globalization.DateTimeStyles]::AssumeLocal).ToUniversalTime()
        }
        catch {
            # try next
        }
    }
    return $null
}

function Build-FahrenheitMaps([string]$callMapPath) {
    $byAddress = @{}
    $bySymbol = @{}
    if (-not (Test-Path $callMapPath)) {
        return @{
            ByAddress = $byAddress
            BySymbol = $bySymbol
            LastWriteUtc = $null
        }
    }

    $pattern = [regex]'public const nint __addr_(?<name>[A-Za-z0-9_]+)\s*=\s*0x(?<hex>[0-9A-Fa-f]+);'
    foreach ($line in Get-Content $callMapPath) {
        $m = $pattern.Match($line)
        if (-not $m.Success) { continue }
        $symbol = $m.Groups["name"].Value
        $rawHex = $m.Groups["hex"].Value
        $parsed = Parse-HexValue $rawHex
        if ($null -eq $parsed) { continue }
        $hex = Normalize-Hex $parsed
        $bySymbol[$symbol] = $hex
        if (-not $byAddress.ContainsKey($hex)) {
            $byAddress[$hex] = New-Object System.Collections.Generic.List[string]
        }
        $byAddress[$hex].Add($symbol)
    }

    return @{
        ByAddress = $byAddress
        BySymbol = $bySymbol
        LastWriteUtc = (Get-Item $callMapPath).LastWriteTimeUtc
    }
}

function Build-LocalOffsetMap([string]$offsetMapPath) {
    $byAddress = @{}
    if (-not (Test-Path $offsetMapPath)) { return $byAddress }

    $pattern = [regex]'public const (?:int|nint)\s+(?<name>[A-Za-z0-9_]+)\s*=\s*0x(?<hex>[0-9A-Fa-f]+);'
    foreach ($line in Get-Content $offsetMapPath) {
        $m = $pattern.Match($line)
        if (-not $m.Success) { continue }
        $name = $m.Groups["name"].Value
        $rawHex = $m.Groups["hex"].Value
        $parsed = Parse-HexValue $rawHex
        if ($null -eq $parsed) { continue }
        $hex = Normalize-Hex $parsed
        if (-not $byAddress.ContainsKey($hex)) {
            $byAddress[$hex] = New-Object System.Collections.Generic.List[string]
        }
        $byAddress[$hex].Add($name)
    }
    return $byAddress
}

function New-ReferenceRecord(
    [string]$sourceGroup,
    [string]$sourcePath,
    [nullable[int]]$lineNumber,
    [nullable[datetime]]$timestampUtc,
    [string]$context,
    [string]$token
) {
    $ts = $null
    if ($null -ne $timestampUtc -and $timestampUtc.HasValue) {
        $ts = $timestampUtc.Value.ToString("o")
    }

    return [pscustomobject]@{
        source_group = $sourceGroup
        source_path = (Normalize-SourcePath $sourcePath)
        line = $lineNumber
        timestamp_utc = $ts
        token = (Normalize-Token $token)
        context = $context
        symbol_hints = @(Extract-SymbolHints $context)
    }
}

Ensure-Dir $OutputDir

$fahrenheit = Build-FahrenheitMaps $FahrenheitCallMap
$localByAddress = Build-LocalOffsetMap $LocalOffsetMap

$raw = New-Object System.Collections.Generic.List[object]
$discordTimestampByTokenAndContext = @{}

if (Test-Path $DiscordCompactCsv) {
    $rows = Import-Csv $DiscordCompactCsv
    foreach ($row in $rows) {
        $content = $row.Content
        if ([string]::IsNullOrWhiteSpace($content)) { continue }
        if ($content -notmatch '(?i)(0x[0-9a-f]{4,10}|FUN_[0-9A-F]{6,10}|FFX\.exe\s*\+\s*0x|pointer|offset|hook|address|Globals\.btl|battle_state|battle_end_type)') {
            continue
        }

        $tokens = Split-TokenValues $content
        if ($tokens.Count -eq 0) { continue }
        $timestampUtc = Parse-TimestampOrNull $row.Timestamp
        foreach ($token in $tokens) {
            $ref = New-ReferenceRecord "discord-compact" $row.SourceFile $null $timestampUtc $content $token
            $raw.Add($ref)
            if ($timestampUtc) {
                $k = ("{0}|{1}" -f $token, $content.Trim())
                if (-not $discordTimestampByTokenAndContext.ContainsKey($k)) {
                    $discordTimestampByTokenAndContext[$k] = $timestampUtc
                }
            }
        }
    }
}

    $searchPattern = '(?i)(0x[0-9a-f]{4,10}|FUN_[0-9A-F]{6,10}|FFX\.exe\s*\+\s*0x|__addr_|pointer|offset|hook|address|Globals\.btl|battle_state|battle_end_type)'
$roots = @(
    ".workspace/Discord",
    ".workspace/research",
    ".workspace/analysis",
    ".workspace/data"
)
$includeGlobs = @("*.txt","*.md","*.csv","*.json","*.cs","*.py","*.ini","*.xml","*.yml","*.yaml")

$rgArgs = @("-n","-i","-S","--no-heading","--color","never")
foreach ($g in $includeGlobs) { $rgArgs += @("-g",$g) }
$rgArgs += @("-g","!discord_messages_compact.csv")
$rgArgs += @("-e",$searchPattern)
$rgArgs += $roots

try {
    $rgOutput = & rg @rgArgs 2>$null
}
catch {
    $rgOutput = @()
}

foreach ($line in $rgOutput) {
    if ($line -notmatch '^(?<file>.+?):(?<ln>\d+):(?<text>.*)$') { continue }
    $file = $Matches["file"]
    $lineNo = [int]$Matches["ln"]
    $text = $Matches["text"]
    $tokens = Split-TokenValues $text
    foreach ($token in $tokens) {
        $raw.Add((New-ReferenceRecord "rg-scan" $file $lineNo $null $text $token))
    }
}

# Deduplicate exact repeats.
$dedup = @{}
foreach ($r in $raw) {
    $k = "{0}|{1}|{2}|{3}" -f $r.source_path, $r.line, $r.token, $r.context
    if (-not $dedup.ContainsKey($k)) { $dedup[$k] = $r }
}
$rawUnique = $dedup.Values

$normalized = New-Object System.Collections.Generic.List[object]
$filtered = New-Object System.Collections.Generic.List[object]
$filteredActionable = New-Object System.Collections.Generic.List[object]

function Is-ActionableReference([string]$token, [string]$context, [string]$sourcePath, [string[]]$addressCandidates) {
    if ([string]::IsNullOrWhiteSpace($token)) { return $false }

    if ($token -match '^FUN_[0-9A-Fa-f]{6,10}$') { return $true }
    if ($token -match '^FFX\.exe\s*\+\s*0x[0-9A-Fa-f]{4,10}$') { return $true }

    $strongContext = $context -match '(?i)\b(pointer|offset|hook|address|globals\.btl|battle_state|battle_end_type|fhmethodhandle|ptr_at|__addr_)\b'
    $isCodeSource = $sourcePath -match '(?i)\.(cs|py)$'

    if ($token -match '^0x([0-9A-Fa-f]{4,10})$') {
        $value = Parse-HexValue $Matches[1]
        if ($null -eq $value) { return $false }

        $excludeValues = @(
            (Parse-HexValue "FFFFFFFF"),
            (Parse-HexValue "7FFFFFFF"),
            (Parse-HexValue "FFFF"),
            (Parse-HexValue "FF"),
            (Parse-HexValue "400000"),
            (Parse-HexValue "4000"),
            (Parse-HexValue "8000"),
            (Parse-HexValue "8001"),
            (Parse-HexValue "800A")
        ) | Where-Object { $null -ne $_ }
        if ($excludeValues -contains $value -and -not $strongContext) { return $false }

        if ($value -ge 0x100000) { return $true }
        if ($strongContext -and $value -ge 0x1000) { return $true }
        if ($isCodeSource -and $strongContext -and $value -ge 0x1000) { return $true }
    }

    if ($addressCandidates.Count -gt 0 -and $strongContext) { return $true }
    return $false
}

foreach ($r in $rawUnique) {
    if ($null -eq $r.timestamp_utc -and $r.source_path -match '(?i)\.workspace[\\/]+Discord') {
        $k = ("{0}|{1}" -f $r.token, $r.context.Trim())
        if ($discordTimestampByTokenAndContext.ContainsKey($k)) {
            $r.timestamp_utc = $discordTimestampByTokenAndContext[$k].ToString("o")
        }
    }

    $addressCandidates = Get-AddressCandidates $r.token
    $matchedLocal = New-Object System.Collections.Generic.List[string]
    $matchedFahrenheitSymbols = New-Object System.Collections.Generic.List[string]

    foreach ($cand in $addressCandidates) {
        if ($localByAddress.ContainsKey($cand)) {
            foreach ($name in $localByAddress[$cand]) { $matchedLocal.Add($name) }
        }
        if ($fahrenheit.ByAddress.ContainsKey($cand)) {
            foreach ($name in $fahrenheit.ByAddress[$cand]) { $matchedFahrenheitSymbols.Add($name) }
        }
    }

    $classification = "new-candidate"
    $excludeReason = $null

    if ($matchedLocal.Count -gt 0 -or $matchedFahrenheitSymbols.Count -gt 0) {
        $classification = "duplicate-known"
        $excludeReason = "already-mapped"
    }

    # Detect symbol/address conflicts where Fahrenheit has a different address for mentioned symbol.
    $hintSymbolSet = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::Ordinal)
    foreach ($s in @($r.symbol_hints)) {
        if (-not [string]::IsNullOrWhiteSpace($s)) { [void]$hintSymbolSet.Add($s) }
    }
    if ($r.token -match '^(FUN_[0-9A-Fa-f]{6,10}|(?:Ms|TO|Atel|Sg|Dmg|Btl|Chr|ei|Fh)[A-Za-z0-9_]+)$') {
        [void]$hintSymbolSet.Add($r.token)
    }
    $hintSymbols = @($hintSymbolSet)
    foreach ($hint in $hintSymbols) {
        if (-not $fahrenheit.BySymbol.ContainsKey($hint)) { continue }
        $knownAddr = $fahrenheit.BySymbol[$hint]
        if ($addressCandidates.Count -eq 0) { continue }
        if ($addressCandidates -contains $knownAddr) { continue }

        if ($r.timestamp_utc -and $fahrenheit.LastWriteUtc) {
            try {
                $msgTime = [datetime]::Parse($r.timestamp_utc)
                if ($msgTime -lt $fahrenheit.LastWriteUtc) {
                    $classification = "superseded-by-fahrenheit"
                    $excludeReason = "discord-older-than-current-fahrenheit"
                    break
                }
            }
            catch {
                # fall through to conflict classification below
            }
        }

        $classification = "conflict-with-fahrenheit"
        $excludeReason = "symbol-address-mismatch"
        break
    }

    $confidence = "low"
    if ($classification -eq "duplicate-known") {
        $confidence = "high"
    }
    elseif ($r.source_group -eq "rg-scan" -and $r.source_path -match '\.(cs|py)$') {
        $confidence = "medium"
    }
    elseif ($r.source_group -eq "discord-compact" -and $r.context -match '(?i)(FFX\.exe\s*\+|FUN_[0-9A-F]{6,10}|Globals\.btl|hook)') {
        $confidence = "medium"
    }

    $entry = [pscustomobject]@{
        source_group = $r.source_group
        source_path = $r.source_path
        line = $r.line
        timestamp_utc = $r.timestamp_utc
        token = $r.token
        address_candidates = @($addressCandidates)
        symbol_hints = @($hintSymbols)
        classification = $classification
        exclude_reason = $excludeReason
        confidence = $confidence
        context = $r.context
        matched_local_symbols = @($matchedLocal | Select-Object -Unique)
        matched_fahrenheit_symbols = @($matchedFahrenheitSymbols | Select-Object -Unique)
        actionable = (Is-ActionableReference $r.token $r.context $r.source_path $addressCandidates)
    }

    $normalized.Add($entry)
    if ($classification -eq "new-candidate" -or $classification -eq "conflict-with-fahrenheit") {
        $filtered.Add($entry)
        $isAnalysisArtifact = $entry.source_path -match '^(?i)\.workspace[\\/]+analysis[\\/]'
        if ($entry.actionable -and -not $isAnalysisArtifact) {
            $filteredActionable.Add($entry)
        }
    }
}

$rawJsonlPath = Join-Path $OutputDir "pointer-hook-references.raw.jsonl"
$normalizedJsonPath = Join-Path $OutputDir "pointer-hook-references.normalized.json"
$filteredJsonPath = Join-Path $OutputDir "pointer-hook-references.filtered.json"
$filteredCsvPath = Join-Path $OutputDir "pointer-hook-references.filtered.csv"
$filteredActionableJsonPath = Join-Path $OutputDir "pointer-hook-references.filtered-actionable.json"
$filteredActionableCsvPath = Join-Path $OutputDir "pointer-hook-references.filtered-actionable.csv"
$newCandidatesJsonPath = Join-Path $OutputDir "pointer-hook-references.new-candidates.json"
$newCandidatesCsvPath = Join-Path $OutputDir "pointer-hook-references.new-candidates.csv"
$curatedJsonPath = Join-Path $OutputDir "pointer-hook-references.curated-candidates.json"
$curatedCsvPath = Join-Path $OutputDir "pointer-hook-references.curated-candidates.csv"
$summaryPath = Join-Path $OutputDir "pointer-hook-references.summary.md"

if (Test-Path $rawJsonlPath) { Remove-Item $rawJsonlPath -Force }
foreach ($r in $rawUnique) {
    ($r | ConvertTo-Json -Depth 8 -Compress) | Add-Content -Path $rawJsonlPath
}

$normalized | ConvertTo-Json -Depth 10 | Set-Content -Path $normalizedJsonPath
$filtered | ConvertTo-Json -Depth 10 | Set-Content -Path $filteredJsonPath
$filtered | Select-Object source_group,source_path,line,timestamp_utc,token,classification,exclude_reason,confidence,@{N="address_candidates";E={($_.address_candidates -join ";")}},@{N="symbol_hints";E={($_.symbol_hints -join ";")}} | Export-Csv -NoTypeInformation -Path $filteredCsvPath
$filteredActionable | ConvertTo-Json -Depth 10 | Set-Content -Path $filteredActionableJsonPath
$filteredActionable | Select-Object source_group,source_path,line,timestamp_utc,token,classification,exclude_reason,confidence,@{N="address_candidates";E={($_.address_candidates -join ";")}},@{N="symbol_hints";E={($_.symbol_hints -join ";")}} | Export-Csv -NoTypeInformation -Path $filteredActionableCsvPath
$newCandidates = $filteredActionable | Where-Object { $_.classification -eq "new-candidate" }
$newCandidates | ConvertTo-Json -Depth 10 | Set-Content -Path $newCandidatesJsonPath
$newCandidates | Select-Object source_group,source_path,line,timestamp_utc,token,classification,exclude_reason,confidence,@{N="address_candidates";E={($_.address_candidates -join ";")}},@{N="symbol_hints";E={($_.symbol_hints -join ";")}} | Export-Csv -NoTypeInformation -Path $newCandidatesCsvPath
$noiseTokens = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
foreach ($noise in @("0xFFFFFFFF","0x7FFFFFFF","0xFFFF","0xFF","0x400000","0xC0000005","0x55555556","0x80000000","0x80000001","0x80808080")) {
    [void]$noiseTokens.Add($noise)
}
$curatedCandidates = $newCandidates | Where-Object {
    if ($_.confidence -ne "medium") { return $false }
    if ($noiseTokens.Contains($_.token)) { return $false }

    $isCodeBacked = $_.source_path -match '^\.workspace/(research/.+\.(cs|py)|Discord/.+json_Files/.+\.(cs|py))$'
    $isDiscordInline = $_.source_path -match '^\.workspace/Discord/.+\.json$' -and $_.context -match '(?i)\b(hook|offset|address|ffx\.exe\s*\+|__addr_)\b'
    if (-not ($isCodeBacked -or $isDiscordInline)) { return $false }

    if ($_.token -match '^0x([0-9A-Fa-f]{4,10})$') {
        $value = Parse-HexValue $Matches[1]
        if ($null -eq $value) { return $false }
        if ($value -lt 0x100000 -and $_.context -notmatch '(?i)\b(offset|address|ffx\.exe\s*\+|hook|__addr_)\b') { return $false }
    }
    return $true
}
$curatedCandidates | ConvertTo-Json -Depth 10 | Set-Content -Path $curatedJsonPath
$curatedCandidates | Select-Object source_group,source_path,line,timestamp_utc,token,classification,exclude_reason,confidence,@{N="address_candidates";E={($_.address_candidates -join ";")}},@{N="symbol_hints";E={($_.symbol_hints -join ";")}} | Export-Csv -NoTypeInformation -Path $curatedCsvPath

$statsRaw = $rawUnique.Count
$statsNorm = $normalized.Count
$statsFiltered = $filtered.Count
$statsFilteredActionable = $filteredActionable.Count
$statsNewCandidates = $newCandidates.Count
$statsCurated = $curatedCandidates.Count
$dupCount = ($normalized | Where-Object { $_.classification -eq "duplicate-known" }).Count
$supersededCount = ($normalized | Where-Object { $_.classification -eq "superseded-by-fahrenheit" }).Count
$conflictCount = ($normalized | Where-Object { $_.classification -eq "conflict-with-fahrenheit" }).Count

$topTokens = $filtered |
    Group-Object token |
    Sort-Object Count -Descending |
    Select-Object -First 25

$lines = @()
$lines += "# Pointer/Hook Mining Summary"
$lines += ""
$lines += "- Raw unique references: $statsRaw"
$lines += "- Normalized references: $statsNorm"
$lines += "- Filtered actionable references: $statsFiltered"
$lines += "- Filtered actionable (strict): $statsFilteredActionable"
$lines += "- New candidates (strict, non-duplicates): $statsNewCandidates"
$lines += "- Curated candidates (high-signal shortlist): $statsCurated"
$lines += "- Duplicate known mappings: $dupCount"
$lines += "- Superseded by newer Fahrenheit mapping: $supersededCount"
$lines += "- Conflicts with Fahrenheit symbol/address: $conflictCount"
$lines += ""
$lines += "## Output Files"
$lines += ""
$lines += "- pointer-hook-references.raw.jsonl"
$lines += "- pointer-hook-references.normalized.json"
$lines += "- pointer-hook-references.filtered.json"
$lines += "- pointer-hook-references.filtered.csv"
$lines += "- pointer-hook-references.filtered-actionable.json"
$lines += "- pointer-hook-references.filtered-actionable.csv"
$lines += "- pointer-hook-references.new-candidates.json"
$lines += "- pointer-hook-references.new-candidates.csv"
$lines += "- pointer-hook-references.curated-candidates.json"
$lines += "- pointer-hook-references.curated-candidates.csv"
$lines += ""
$lines += "## Top Filtered Tokens"
$lines += ""
if ($topTokens.Count -eq 0) {
    $lines += "- (none)"
}
else {
    foreach ($t in $topTokens) {
        $lines += "- $($t.Name): $($t.Count)"
    }
}

$lines | Set-Content -Path $summaryPath

Write-Host "Wrote:"
Write-Host "  $rawJsonlPath"
Write-Host "  $normalizedJsonPath"
Write-Host "  $filteredJsonPath"
Write-Host "  $filteredCsvPath"
Write-Host "  $filteredActionableJsonPath"
Write-Host "  $filteredActionableCsvPath"
Write-Host "  $newCandidatesJsonPath"
Write-Host "  $newCandidatesCsvPath"
Write-Host "  $curatedJsonPath"
Write-Host "  $curatedCsvPath"
Write-Host "  $summaryPath"
