#!/bin/bash
set -euo pipefail

echo "🔨 Building Jellyfin Hue Sync Plugin..."
echo ""

if ! command -v dotnet >/dev/null 2>&1; then
    echo "❌ .NET 8 SDK is required (dotnet was not found in PATH)." >&2
    exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
    echo "❌ Python 3 is required to create the deterministic release archive and manifest." >&2
    exit 1
fi

if ! command -v git >/dev/null 2>&1; then
    echo "Git is required to verify release provenance." >&2
    exit 1
fi

# Provenance is bound to the commit, so refuse to package a dirty checkout
# whose uncommitted source files would not be represented by that SHA.
if ! REPOSITORY_ROOT=$(git -c safe.directory="$PWD" rev-parse --show-toplevel); then
    echo "Unable to determine the Git checkout root." >&2
    exit 1
fi
SCRIPT_ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
if [ -z "$REPOSITORY_ROOT" ] || [ ! -d "$REPOSITORY_ROOT" ] ||
    [ "$(cd -- "$REPOSITORY_ROOT" && pwd -P)" != "$SCRIPT_ROOT" ] ||
    [ "$(pwd -P)" != "$SCRIPT_ROOT" ] ||
    [ ! -f ./Jellyfin.Plugin.Hue.sln ] || [ ! -f ./Jellyfin.Plugin.Hue/Jellyfin.Plugin.Hue.csproj ] ||
    [ ! -f ./meta.json ]; then
    echo "Release helper must run from its plugin repository root." >&2
    exit 1
fi
if ! GIT_STATUS=$(git -c safe.directory="$PWD" status --porcelain=v1 --untracked-files=all); then
    echo "Unable to establish a clean Git checkout." >&2
    exit 1
fi
if [ -n "$GIT_STATUS" ]; then
    echo "❌ Release helper requires a clean Git checkout; commit or remove local changes first." >&2
    exit 1
fi
if ! SOURCE_COMMIT=$(git -c safe.directory="$PWD" rev-parse --verify HEAD); then
    echo "Unable to resolve the checked-out source commit." >&2
    exit 1
fi
if [[ ! "$SOURCE_COMMIT" =~ ^[0-9a-f]{40}$ ]]; then
    echo "The checked-out source commit must be a 40-character Git SHA." >&2
    exit 1
fi
if ! SOURCE_TYPE=$(git -c safe.directory="$PWD" cat-file -t "$SOURCE_COMMIT") || [ "$SOURCE_TYPE" != "commit" ]; then
    echo "The checked-out source SHA must identify a commit object." >&2
    exit 1
fi

if ! command -v unzip >/dev/null 2>&1; then
    echo "❌ unzip is required to verify the release archive." >&2
    exit 1
fi

# Clean previous builds only after the provenance guard. A failed invocation
# from a dirty checkout must not destroy an operator's existing artifacts.
echo "📦 Cleaning previous builds..."
rm -rf ./Jellyfin.Plugin.Hue/bin/Release
rm -rf ./release-package
rm -rf ./publish
rm -f jellyfin-plugin-hue-*.zip
rm -f jellyfin-plugin-hue-*.zip.sha256
rm -f jellyfin-plugin-hue-*.manifest.json
rm -f jellyfin-plugin-hue-*.manifest.json.sha256

# Restore dependencies
echo "📥 Restoring dependencies..."
dotnet restore --locked-mode

# Build in Release mode
echo "🏗️  Building project..."
dotnet build --configuration Release --no-restore

# Run tests
echo "🧪 Running tests..."
dotnet test --configuration Release --no-build --verbosity minimal

# Publish the plugin so managed package dependencies are copied beside the assembly.
# CopyLocalLockFileAssemblies is intentionally disabled for ordinary builds; the
# publish directory is therefore the authoritative source for the release archive.
echo "📤 Publishing plugin dependencies..."
dotnet publish Jellyfin.Plugin.Hue/Jellyfin.Plugin.Hue.csproj \
    --configuration Release \
    --no-build \
    --output ./publish

# Extract and validate the release version from both sources of truth.
VERSION=$(python3 -c 'import json, re, sys; value=json.load(open(sys.argv[1], encoding="utf-8")).get("version"); print(value) if isinstance(value, str) and re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+", value) else sys.exit("meta.json version must be a four-part numeric version")' meta.json)
PROJECT_VERSION=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' Jellyfin.Plugin.Hue/Jellyfin.Plugin.Hue.csproj | head -n 1)
PUBLISHED_VERSION=$(python3 -c 'import json, re, sys; value=json.load(open(sys.argv[1], encoding="utf-8")).get("version"); print(value) if isinstance(value, str) and re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+", value) else sys.exit("published meta.json version must be a four-part numeric version")' publish/meta.json)
printf '%s\n' "$PROJECT_VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$'
if [ -z "$VERSION" ] || [ "$VERSION" != "$PROJECT_VERSION" ] || [ "$VERSION" != "$PUBLISHED_VERSION" ]; then
    echo "❌ Version mismatch: meta.json=$VERSION project=$PROJECT_VERSION published=$PUBLISHED_VERSION" >&2
    exit 1
fi

if [ ! -f publish/Jellyfin.Plugin.Hue.dll ]; then
    echo "❌ Release DLL was not produced." >&2
    exit 1
fi
if [ ! -f publish/BouncyCastle.Cryptography.dll ]; then
    echo "❌ Managed DTLS dependency was not produced." >&2
    exit 1
fi

# Create release package directory
echo "📦 Creating release package..."
mkdir -p release-package

# Copy only the required files after validating the publish output.
cp publish/Jellyfin.Plugin.Hue.dll release-package/
cp publish/BouncyCastle.Cryptography.dll release-package/
cp publish/meta.json release-package/
cp LICENSE NOTICE release-package/
echo ""
echo "📋 Package Information:"
echo "   Version: $VERSION"
echo "   Files:"
ls -lh release-package/

# Create the canonical deterministic ZIP archive. The helper fixes entry
# order, timestamps, host metadata, and DEFLATE settings for cross-platform
# reproducibility.
ZIPFILE="jellyfin-plugin-hue-v${VERSION}.zip"
echo ""
echo "🗜️  Creating archive: $ZIPFILE"
python3 scripts/create-deterministic-release-zip.py \
    --input-dir release-package \
    --output "$ZIPFILE"

ARCHIVE_FILES=$(unzip -Z1 "$ZIPFILE" | tr '\n' ' ')
if [ "$ARCHIVE_FILES" != "BouncyCastle.Cryptography.dll Jellyfin.Plugin.Hue.dll LICENSE NOTICE meta.json " ]; then
    echo "❌ Unexpected release archive contents: $ARCHIVE_FILES" >&2
    exit 1
fi

CHECKSUM_FILE="$ZIPFILE.sha256"
sha256sum "$ZIPFILE" > "$CHECKSUM_FILE"
sha256sum --check --strict "$CHECKSUM_FILE"

# Publish a deterministic manifest beside the archive. It records the exact
# package-file hashes, archive hash, and hash-locked NuGet graph so downstream
# operators can audit the release without trusting the build host.
MANIFEST_FILE="jellyfin-plugin-hue-v${VERSION}.manifest.json"
python3 scripts/create-release-manifest.py \
    --package-dir release-package \
    --lock-file Jellyfin.Plugin.Hue/packages.lock.json \
    --archive "$ZIPFILE" \
    --version "$VERSION" \
    --source-commit "$SOURCE_COMMIT" \
    --output "$MANIFEST_FILE"
MANIFEST_CHECKSUM_FILE="$MANIFEST_FILE.sha256"
sha256sum "$MANIFEST_FILE" > "$MANIFEST_CHECKSUM_FILE"
sha256sum --check --strict "$MANIFEST_CHECKSUM_FILE"

echo ""
echo "✅ Build complete!"
echo ""
echo "📁 Release package: $ZIPFILE"
echo "   Size: $(ls -lh $ZIPFILE | awk '{print $5}')"
echo "   Checksum: $CHECKSUM_FILE"
echo "   Release manifest: $MANIFEST_FILE"
echo "   Manifest checksum: $MANIFEST_CHECKSUM_FILE"
echo ""
echo "🚀 Installation:"
echo "   1. Extract $ZIPFILE to your Jellyfin plugins directory"
echo "   2. Create a folder named 'HueSync_${VERSION}' if it doesn't exist"
echo "   3. Place the extracted files inside"
echo "   4. Restart Jellyfin"
echo ""
