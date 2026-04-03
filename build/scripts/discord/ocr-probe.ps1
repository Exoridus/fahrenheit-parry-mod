param([string]$ImagePath,[string]$Model)
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($ImagePath)
try {
  $maxDimension = 640
  $scale = [Math]::Min($maxDimension / $img.Width, $maxDimension / $img.Height)
  if ($scale -gt 1) { $scale = 1 }
  $width = [int][Math]::Round($img.Width * $scale)
  $height = [int][Math]::Round($img.Height * $scale)
  $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ('ocr_probe_' + [System.Guid]::NewGuid().ToString('N') + '.jpg')
  $bmp = New-Object System.Drawing.Bitmap $width, $height
  $graphics = [System.Drawing.Graphics]::FromImage($bmp)
  try {
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($img, 0, 0, $width, $height)
    $bmp.Save($tempPath, [System.Drawing.Imaging.ImageFormat]::Jpeg)
  } finally { $graphics.Dispose(); $bmp.Dispose() }
  $bytes = [System.IO.File]::ReadAllBytes($tempPath)
  $base64 = [Convert]::ToBase64String($bytes)
  $token = [Environment]::GetEnvironmentVariable('LMSTUDIO_API_KEY')
  $body = @{ model=$Model; temperature=0; max_tokens=220; messages=@(@{ role='user'; content=@(@{ type='text'; text='Return only minified JSON with keys classification,text,confidence,notes. classification must be one of no_text, full_text, needs_review. Ignore tiny incidental text on clothing, logos, decorative art, watermarks, and background clutter unless it is the main foreground subject. Use no_text if there is no useful foreground text worth OCR. Use full_text only if all useful visible foreground text is clearly readable and exactly transcribed. Otherwise use needs_review. confidence must be an integer 0-100.' }, @{ type='image_url'; image_url=@{ url=('data:image/jpeg;base64,' + $base64) } }) }) } | ConvertTo-Json -Depth 12
  $resp = Invoke-WebRequest -Uri 'http://10.0.20.40:1234/v1/chat/completions' -Method Post -Headers @{ Authorization = ('Bearer ' + $token); 'Content-Type'='application/json' } -Body $body -TimeoutSec 240 -SkipHttpErrorCheck
  [pscustomobject]@{ Model=$Model; ImagePath=$ImagePath; StatusCode=[int]$resp.StatusCode; Content=$resp.Content } | ConvertTo-Json -Depth 5
  Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
} finally { $img.Dispose() }
