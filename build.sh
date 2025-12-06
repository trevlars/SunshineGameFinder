#!/bin/bash

# Build script for SunshineGameFinder Linux executable

# Add .NET to PATH if installed in home directory
if [ -d "$HOME/.dotnet" ]; then
    export PATH="$HOME/.dotnet:$PATH"
fi

echo "Building SunshineGameFinder for Linux..."

# Detect architecture
ARCH=$(uname -m)
if [ "$ARCH" = "x86_64" ]; then
    RID="linux-x64"
elif [ "$ARCH" = "aarch64" ]; then
    RID="linux-arm64"
else
    echo "Unknown architecture: $ARCH"
    echo "Defaulting to linux-x64"
    RID="linux-x64"
fi

echo "Target runtime: $RID"

# Build self-contained single-file executable
dotnet publish SunshineGameFinder/SunshineGameFinder.csproj \
    -c Release \
    -r $RID \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o ./publish/$RID

echo ""
echo "Build complete! Executable is located at: ./publish/$RID/SunshineGameFinder"
echo ""
echo "To run it:"
echo "  ./publish/$RID/SunshineGameFinder"
echo ""
echo "Or copy it to a location in your PATH:"
echo "  sudo cp ./publish/$RID/SunshineGameFinder /usr/local/bin/sunshine-game-finder"

