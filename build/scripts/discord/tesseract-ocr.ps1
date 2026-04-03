param(
    [string]$ImagePath,
    [string]$TesseractPath = ".workspace/tools/tesseract/tesseract.exe",
    [int]$MinDimension = 640
)

if (-not (Test-Path $ImagePath)) {
    Write-Error "Image path not found: $ImagePath"
    exit 1
}

if (-not (Test-Path $TesseractPath)) {
    Write-Warning "Tesseract not found at $TesseractPath. Skipping Tesseract OCR."
    exit 0
}

$sidecar = $ImagePath + ".ocr.txt"
if (Test-Path $sidecar) {
    exit 0
}

try {
    Add-Type -AssemblyName System.Drawing | Out-Null
    $img = [System.Drawing.Image]::FromFile($ImagePath)
    try {
        if ([Math]::Max($img.Width, $img.Height) -lt $MinDimension) {
            exit 0
        }
    }
    finally {
        $img.Dispose()
    }
}
catch {
    Write-Warning "Failed to inspect image dimensions for $ImagePath. Continuing OCR."
}

$tempBase = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString("N"))
& $TesseractPath $ImagePath $tempBase -l eng --psm 3 | Out-Null

$tempResult = $tempBase + ".txt"
if (Test-Path $tempResult) {
    $text = Get-Content $tempResult -Raw
    [System.IO.File]::WriteAllText($sidecar, $text)
    Remove-Item $tempResult -Force
}
