# Builds self-contained single-file apps for Windows 64-bit (Intel/AMD) and Windows on ARM into .\dist
# Usage:   .\build.ps1                 (self-contained: runs on any Windows 10 2004+/11, ~70 MB)
#          .\build.ps1 -Small          (needs the .NET 8 Desktop Runtime installed, ~5 MB)
param([switch]$Small)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "The .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
    exit 1
}

foreach ($rid in "win-x64", "win-arm64") {
    Write-Host "==> Building $rid" -ForegroundColor Cyan
    $out = "dist\$rid"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    if ($Small) {
        dotnet publish -c Release -r $rid --self-contained false -p:PublishSingleFile=true -o $out
    } else {
        dotnet publish -c Release -r $rid --self-contained true -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
    }
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $rid" }

    Compress-Archive -Path "$out\AudiobookPlayer.exe" -DestinationPath "dist\AudiobookPlayer-$rid.zip" -Force
}

Write-Host ""
Write-Host "Done:" -ForegroundColor Green
Write-Host "  dist\win-x64\AudiobookPlayer.exe     (most PCs)"
Write-Host "  dist\win-arm64\AudiobookPlayer.exe   (Windows on ARM)"
Write-Host "  dist\AudiobookPlayer-*.zip           (ready to share)"
