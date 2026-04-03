$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

$env:BUILD_CMD_SMOKE_ONLY = '1'
$env:BUILD_CMD_ALLOW_NESTED = '1'

$checks = @(
    @{ Command = '.\\build.cmd --help'; MustContain = @('[NUKE] dotnet run --project build\Build.csproj -- --target Help') },
    @{ Command = '.\\build.cmd -h deploy'; MustContain = @('[NUKE] dotnet run --project build\Build.csproj -- --target Help --workflow deploy') },
    @{ Command = '.\\build.cmd deploy -h'; MustContain = @('[NUKE] dotnet run --project build\Build.csproj -- --target Help --workflow deploy') },
    @{ Command = '.\\build.cmd build --no-auto-deploy --target Debug'; MustContain = @('[NUKE] dotnet run --project build\Build.csproj -- --target Cli --workflow build --no-auto-deploy --build-target Debug') },
    @{ Command = '.\\build.cmd --target Help --workflow build --dry-run'; MustContain = @('[NUKE] dotnet run --project build\Build.csproj -- --target Help --workflow build --dry-run') },
    @{ Command = '.\\build.cmd --target Help --workflow deploy --game-dir "C:\\Program Files\\Square Enix\\Final Fantasy X-X2 - HD Remaster"'; MustContain = @('--target Help --workflow deploy --game-dir') }
)

foreach ($check in $checks) {
    Write-Host "[CLI-SMOKE] $($check.Command)"
    $output = cmd /c $check.Command 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed ($LASTEXITCODE): $($check.Command)`n$output"
    }

    $mustContain = @()
    if ($null -ne $check.MustContain) { $mustContain = $check.MustContain }
    foreach ($needle in $mustContain) {
        if ($output -notmatch [Regex]::Escape($needle)) {
            throw "Expected output to contain '$needle' for command: $($check.Command)`n$output"
        }
    }

    $mustNotContain = @()
    if ($null -ne $check.MustNotContain) { $mustNotContain = $check.MustNotContain }
    foreach ($needle in $mustNotContain) {
        if ($output -match [Regex]::Escape($needle)) {
            throw "Expected output to NOT contain '$needle' for command: $($check.Command)`n$output"
        }
    }
}

Write-Host '[CLI-SMOKE] All CLI checks passed.'
$env:BUILD_CMD_SMOKE_ONLY = $null
$env:BUILD_CMD_ALLOW_NESTED = $null
