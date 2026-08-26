# PowerShell build script for Jellyfin Hue Sync Plugin
$ErrorActionPreference = "Stop"

Write-Host "🔨 Building Jellyfin Hue Sync Plugin..." -ForegroundColor Cyan
Write-Host ""

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 8 SDK is required (dotnet was not found in PATH)."
}
$pythonCommand = @("python", "python3") |
    Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } |
    Select-Object -First 1
if (-not $pythonCommand) {
    throw "Python 3 is required to create the deterministic release archive."
}

# Clean previous builds
Write-Host "📦 Cleaning previous builds..." -ForegroundColor Yellow
if (Test-Path "./Jellyfin.Plugin.Hue/bin/Release") {
    Remove-Item -Recurse -Force "./Jellyfin.Plugin.Hue/bin/Release"
}
if (Test-Path "./release-package") {
    Remove-Item -Recurse -Force "./release-package"
}
if (Test-Path "./publish") {
    Remove-Item -Recurse -Force "./publish"
}
Get-ChildItem -Filter "jellyfin-plugin-hue-*.zip" | Remove-Item -Force
Get-ChildItem -Filter "jellyfin-plugin-hue-*.zip.sha256" | Remove-Item -Force

# Restore dependencies
Write-Host "📥 Restoring dependencies..." -ForegroundColor Yellow
dotnet restore --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Build in Release mode
Write-Host "🏗️  Building project..." -ForegroundColor Yellow
dotnet build --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Run tests
Write-Host "🧪 Running tests..." -ForegroundColor Yellow
dotnet test --configuration Release --no-build --verbosity minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Publish the plugin so managed package dependencies are copied beside the assembly.
# CopyLocalLockFileAssemblies is intentionally disabled for ordinary builds; the
# publish directory is therefore the authoritative source for the release archive.
Write-Host "📤 Publishing plugin dependencies..." -ForegroundColor Yellow
dotnet publish Jellyfin.Plugin.Hue/Jellyfin.Plugin.Hue.csproj --configuration Release --no-build --output ./publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Extract and validate the version from JSON rather than matching arbitrary text.
$metaContent = Get-Content "meta.json" -Raw | ConvertFrom-Json
$version = [string]$metaContent.version
$versionPattern = '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$'
if ($version -notmatch $versionPattern) {
    throw "meta.json version must be a four-part numeric version."
}
$projectContent = Get-Content "Jellyfin.Plugin.Hue/Jellyfin.Plugin.Hue.csproj" -Raw
$projectVersion = ($projectContent | Select-String '<Version>([^<]+)</Version>').Matches.Groups[1].Value
$publishedMetaContent = Get-Content "publish/meta.json" -Raw | ConvertFrom-Json
$publishedVersion = [string]$publishedMetaContent.version
if ($projectVersion -notmatch $versionPattern -or $publishedVersion -notmatch $versionPattern) {
    throw "Project and published versions must be four-part numeric versions."
}
if ([string]::IsNullOrWhiteSpace($version) -or $version -ne $projectVersion -or $version -ne $publishedVersion) {
    throw "Version mismatch: meta.json=$version project=$projectVersion published=$publishedVersion"
}

$dllPath = "publish/Jellyfin.Plugin.Hue.dll"
if (-not (Test-Path $dllPath)) {
    throw "Release DLL was not produced."
}
if (-not (Test-Path "publish/BouncyCastle.Cryptography.dll")) {
    throw "Managed DTLS dependency was not produced."
}

# Create release package directory after validating the publish output.
Write-Host "📦 Creating release package..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path "./release-package" | Out-Null

# Copy only the required files
Copy-Item "publish/Jellyfin.Plugin.Hue.dll" "release-package/"
Copy-Item "publish/BouncyCastle.Cryptography.dll" "release-package/"
Copy-Item "publish/meta.json" "release-package/"

Write-Host ""
Write-Host "📋 Package Information:" -ForegroundColor Cyan
Write-Host "   Version: $version"
Write-Host "   Files:" -ForegroundColor White
Get-ChildItem "release-package" | ForEach-Object {
    Write-Host "   - $($_.Name) ($([math]::Round($_.Length/1KB, 2)) KB)"
}

# Create the canonical deterministic ZIP archive. The shared helper fixes
# entry order, timestamps, host metadata, and DEFLATE settings on Windows and
# Linux alike.
$zipFile = "jellyfin-plugin-hue-v$version.zip"
Write-Host ""
Write-Host "🗜️  Creating archive: $zipFile" -ForegroundColor Yellow

& $pythonCommand "scripts/create-deterministic-release-zip.py" `
    --input-dir "release-package" `
    --output $zipFile
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$zipSize = [math]::Round((Get-Item $zipFile).Length/1KB, 2)

$archiveEntries = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $zipFile)).Entries.FullName | Sort-Object
if (($archiveEntries -join ' ') -ne 'BouncyCastle.Cryptography.dll Jellyfin.Plugin.Hue.dll meta.json') {
    throw "Unexpected release archive contents: $($archiveEntries -join ', ')"
}

$checksumFile = $zipFile + ".sha256"
$archiveHash = (Get-FileHash -Algorithm SHA256 -Path $zipFile).Hash.ToLowerInvariant()
($archiveHash + "  " + [System.IO.Path]::GetFileName($zipFile)) | Set-Content -Path $checksumFile -Encoding ascii
$checksumParts = (Get-Content -LiteralPath $checksumFile -Raw).Trim() -split '\s+', 2
$expectedArchiveName = [System.IO.Path]::GetFileName($zipFile)
if ($checksumParts.Count -ne 2 -or
    $checksumParts[0] -notmatch '^[0-9a-fA-F]{64}$' -or
    $checksumParts[1] -ne $expectedArchiveName) {
    throw "Checksum sidecar is malformed or names the wrong archive: $checksumFile"
}

$verifiedHash = (Get-FileHash -Algorithm SHA256 -Path $zipFile).Hash.ToLowerInvariant()
if ($checksumParts[0].ToLowerInvariant() -ne $verifiedHash) {
    throw "Checksum verification failed for $zipFile"
}

Write-Host ""
Write-Host "✅ Build complete!" -ForegroundColor Green
Write-Host ""
Write-Host "📁 Release package: $zipFile" -ForegroundColor Cyan
Write-Host "   Size: $zipSize KB"
Write-Host "   Checksum: $checksumFile"
Write-Host ""
Write-Host "🚀 Installation:" -ForegroundColor Cyan
Write-Host "   1. Extract $zipFile to your Jellyfin plugins directory"
Write-Host "   2. Create a folder named 'HueSync' if it doesn't exist"
Write-Host "   3. Place the extracted files inside"
Write-Host "   4. Restart Jellyfin"
Write-Host ""
