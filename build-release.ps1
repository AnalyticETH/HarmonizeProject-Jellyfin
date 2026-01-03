# PowerShell build script for Jellyfin Hue Sync Plugin
$ErrorActionPreference = "Stop"

Write-Host "🔨 Building Jellyfin Hue Sync Plugin..." -ForegroundColor Cyan
Write-Host ""

# Clean previous builds
Write-Host "📦 Cleaning previous builds..." -ForegroundColor Yellow
if (Test-Path "./Jellyfin.Plugin.Hue/bin/Release") {
    Remove-Item -Recurse -Force "./Jellyfin.Plugin.Hue/bin/Release"
}
if (Test-Path "./release-package") {
    Remove-Item -Recurse -Force "./release-package"
}
Get-ChildItem -Filter "jellyfin-plugin-hue-*.zip" | Remove-Item -Force

# Restore dependencies
Write-Host "📥 Restoring dependencies..." -ForegroundColor Yellow
dotnet restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Build in Release mode
Write-Host "🏗️  Building project..." -ForegroundColor Yellow
dotnet build --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Run tests
Write-Host "🧪 Running tests..." -ForegroundColor Yellow
dotnet test --configuration Release --no-build --verbosity minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Create release package directory
Write-Host "📦 Creating release package..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path "./release-package" | Out-Null

# Copy only the required files
Copy-Item "Jellyfin.Plugin.Hue/bin/Release/net8.0/Jellyfin.Plugin.Hue.dll" "release-package/"
Copy-Item "meta.json" "release-package/"

# Extract version from meta.json
$metaContent = Get-Content "meta.json" -Raw
$version = ($metaContent | Select-String '"version":\s*"([^"]+)"').Matches.Groups[1].Value

Write-Host ""
Write-Host "📋 Package Information:" -ForegroundColor Cyan
Write-Host "   Version: $version"
Write-Host "   Files:" -ForegroundColor White
Get-ChildItem "release-package" | ForEach-Object {
    Write-Host "   - $($_.Name) ($([math]::Round($_.Length/1KB, 2)) KB)"
}

# Create zip archive
$zipFile = "jellyfin-plugin-hue-v$version.zip"
Write-Host ""
Write-Host "🗜️  Creating archive: $zipFile" -ForegroundColor Yellow

# Use .NET compression if available, otherwise use Compress-Archive
if ([System.IO.Compression.ZipFile]) {
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        (Resolve-Path "release-package").Path,
        (Join-Path (Get-Location) $zipFile),
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )
} else {
    Compress-Archive -Path "release-package/*" -DestinationPath $zipFile -Force
}

$zipSize = [math]::Round((Get-Item $zipFile).Length/1KB, 2)

Write-Host ""
Write-Host "✅ Build complete!" -ForegroundColor Green
Write-Host ""
Write-Host "📁 Release package: $zipFile" -ForegroundColor Cyan
Write-Host "   Size: $zipSize KB"
Write-Host ""
Write-Host "🚀 Installation:" -ForegroundColor Cyan
Write-Host "   1. Extract $zipFile to your Jellyfin plugins directory"
Write-Host "   2. Create a folder named 'HueSync' if it doesn't exist"
Write-Host "   3. Place the extracted files inside"
Write-Host "   4. Restart Jellyfin"
Write-Host ""
