#!/bin/bash
set -e

echo "🔨 Building Jellyfin Hue Sync Plugin..."
echo ""

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

# Extract version from meta.json (portable version)
VERSION=$(cat meta.json | grep '"version"' | sed 's/.*"version":[[:space:]]*"\([^"]*\)".*/\1/')
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
