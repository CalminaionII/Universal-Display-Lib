#!/bin/bash
# Sets up a Claude Code cloud session so the mod can be built there.
# Does nothing on your own PC.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

# Keep in step with the game version the mod is built against.
VS_VERSION="1.22.7"
VS_DIR="$HOME/vintagestory-$VS_VERSION"

# 1. The .NET 10 SDK, from Ubuntu's own package archive.
if ! command -v dotnet >/dev/null 2>&1; then
  echo "Installing .NET 10 SDK..."
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-10.0 >/dev/null
fi

# 2. The game's DLLs. Only the .dll files are kept from the download.
#    Needs cdn.vintagestory.at allowed in the environment's network settings.
if [ ! -f "$VS_DIR/VintagestoryAPI.dll" ]; then
  echo "Downloading Vintage Story $VS_VERSION DLLs..."
  mkdir -p "$VS_DIR"
  if ! curl -fsSL "https://cdn.vintagestory.at/gamefiles/stable/vs_client_linux-x64_${VS_VERSION}.tar.gz" \
      | tar -xz -C "$VS_DIR" --strip-components=1 --wildcards '*.dll'; then
    echo "Could not download Vintage Story. Allow cdn.vintagestory.at in the environment's network access settings." >&2
    rm -rf "$VS_DIR"
  fi
fi

# 3. Point the .csproj at those DLLs for the rest of the session.
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  echo "export VINTAGE_STORY=\"$VS_DIR\"" >> "$CLAUDE_ENV_FILE"
  echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1' >> "$CLAUDE_ENV_FILE"
fi
