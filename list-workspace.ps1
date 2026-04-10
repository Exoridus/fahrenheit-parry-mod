New-Item -ItemType Directory -Force .workspace-audit | Out-Null

# Vollständige Dateiliste
Get-ChildItem .workspace -Recurse -File | ForEach-Object {
    [PSCustomObject]@{
        FullName      = $_.FullName
        RelativePath  = $_.FullName.Substring((Resolve-Path .).Path.Length + 1)
        Directory     = $_.DirectoryName
        Extension     = $_.Extension
        SizeBytes     = $_.Length
        LastWriteTime = $_.LastWriteTime
    }
} | Export-Csv .workspace-audit/workspace-files.csv -NoTypeInformation -Encoding UTF8

# Top-Level-Zusammenfassung
Get-ChildItem .workspace | ForEach-Object {
    if ($_.PSIsContainer) {
        $files = Get-ChildItem $_.FullName -Recurse -File -ErrorAction SilentlyContinue
        [PSCustomObject]@{
            Name       = $_.Name
            FileCount  = @($files).Count
            TotalBytes = ($files | Measure-Object Length -Sum).Sum
        }
    } else {
        [PSCustomObject]@{
            Name       = $_.Name
            FileCount  = 1
            TotalBytes = $_.Length
        }
    }
} | Export-Csv .workspace-audit/workspace-top-level.csv -NoTypeInformation -Encoding UTF8

# Dateitypen-Häufigkeit
Import-Csv .workspace-audit/workspace-files.csv |
Group-Object Extension |
ForEach-Object {
    $sum = ($_.Group | Measure-Object SizeBytes -Sum).Sum
    [PSCustomObject]@{
        Extension  = if ([string]::IsNullOrWhiteSpace($_.Name)) { "<no_ext>" } else { $_.Name }
        Count      = $_.Count
        TotalBytes = $sum
    }
} | Sort-Object Count -Descending |
Export-Csv .workspace-audit/workspace-extensions.csv -NoTypeInformation -Encoding UTF8

# Größte Dateien
Import-Csv .workspace-audit/workspace-files.csv |
Sort-Object {[int64]$_.SizeBytes} -Descending |
Select-Object -First 500 |
Export-Csv .workspace-audit/workspace-largest-files.csv -NoTypeInformation -Encoding UTF8