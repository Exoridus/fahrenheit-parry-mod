<#
.SYNOPSIS
    Asserts that the packaged release assets exist and stay within their size budget.

.DESCRIPTION
    Shared by the CI release preflight and the Release workflow so both enforce the
    same limits. Budgets come from the MAX_FULL_ZIP_MB / MAX_MOD_ZIP_MB environment
    variables; the defaults below apply when they are unset.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Tag,

    [Parameter(Mandatory = $true)]
    [string] $Directory,

    [string] $ModId = 'fhparry'
)

$ErrorActionPreference = 'Stop'

$maxFullMb = if ($env:MAX_FULL_ZIP_MB) { [int]$env:MAX_FULL_ZIP_MB } else { 200 }
$maxModMb = if ($env:MAX_MOD_ZIP_MB) { [int]$env:MAX_MOD_ZIP_MB } else { 64 }

$assets = @(
    [pscustomobject]@{ Path = Join-Path $Directory "fahrenheit-full-$Tag.zip"; MaxMb = $maxFullMb }
    [pscustomobject]@{ Path = Join-Path $Directory "$ModId-mod-$Tag.zip"; MaxMb = $maxModMb }
)

foreach ($asset in $assets) {
    if (-not (Test-Path $asset.Path)) {
        throw "Missing release asset: $($asset.Path)"
    }

    $bytes = (Get-Item $asset.Path).Length
    $mb = [math]::Round($bytes / 1MB, 2)

    if ($bytes -gt ($asset.MaxMb * 1MB)) {
        throw "Asset too large: $($asset.Path) is $mb MB > $($asset.MaxMb) MB"
    }

    Write-Host "OK $([System.IO.Path]::GetFileName($asset.Path)) = $mb MB (limit $($asset.MaxMb) MB)"
}

foreach ($asset in $assets) {
    $checksum = "$($asset.Path).sha256"
    if (-not (Test-Path $checksum)) {
        throw "Missing checksum file: $checksum"
    }
}

Write-Host 'Release asset checks passed.'
