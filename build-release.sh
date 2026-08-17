#!/bin/bash
set -e

echo "🔨 Building Jellyfin Hue Sync Plugin..."
echo ""

if ! command -v dotnet >/dev/null 2>&1; then
    echo "❌ .NET 8 SDK is required (dotnet was not found in PATH)." >&2
    exit 1
fi

if ! command -v zip >/dev/null 2>&1; then
    echo "❌ zip is required to create the release archive." >&2
    exit 1
fi

if ! command -v unzip >/dev/null 2>&1; then
    echo "❌ unzip is required to verify the release archive." >&2
    exit 1
fi

# Clean previous builds
echo "📦 Cleaning previous builds..."
rm -rf ./Jellyfin.Plugin.Hue/bin/Release
rm -rf ./release-package
rm -f jellyfin-plugin-hue-*.zip

# Restore dependencies
echo "📥 Restoring dependencies..."
dotnet restore

# Build in Release mode
echo "🏗️  Building project..."
dotnet build --configuration Release --no-restore

# Run tests
echo "🧪 Running tests..."
dotnet test --configuration Release --no-build --verbosity minimal

# Create release package directory
echo "📦 Creating release package..."
mkdir -p release-package

# Copy only the required files
cp Jellyfin.Plugin.Hue/bin/Release/net8.0/Jellyfin.Plugin.Hue.dll release-package/
cp meta.json release-package/

# Extract and validate the release version from both sources of truth.
VERSION=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' meta.json | head -n 1)
PROJECT_VERSION=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' Jellyfin.Plugin.Hue/Jellyfin.Plugin.Hue.csproj | head -n 1)
if [ -z "$VERSION" ] || [ "$VERSION" != "$PROJECT_VERSION" ]; then
    echo "❌ Version mismatch: meta.json=$VERSION project=$PROJECT_VERSION" >&2
    exit 1
fi

if [ ! -f Jellyfin.Plugin.Hue/bin/Release/net8.0/Jellyfin.Plugin.Hue.dll ]; then
    echo "❌ Release DLL was not produced." >&2
    exit 1
fi
echo ""
echo "📋 Package Information:"
echo "   Version: $VERSION"
echo "   Files:"
ls -lh release-package/

# Create zip archive
ZIPFILE="jellyfin-plugin-hue-v${VERSION}.zip"
echo ""
echo "🗜️  Creating archive: $ZIPFILE"
cd release-package
zip -r ../$ZIPFILE .
cd ..

ARCHIVE_FILES=$(unzip -Z1 "$ZIPFILE" | sort | tr '\n' ' ')
if [ "$ARCHIVE_FILES" != "Jellyfin.Plugin.Hue.dll meta.json " ]; then
    echo "❌ Unexpected release archive contents: $ARCHIVE_FILES" >&2
    exit 1
fi

echo ""
echo "✅ Build complete!"
echo ""
echo "📁 Release package: $ZIPFILE"
echo "   Size: $(ls -lh $ZIPFILE | awk '{print $5}')"
echo ""
echo "🚀 Installation:"
echo "   1. Extract $ZIPFILE to your Jellyfin plugins directory"
echo "   2. Create a folder named 'HueSync' if it doesn't exist"
echo "   3. Place the extracted files inside"
echo "   4. Restart Jellyfin"
echo ""
