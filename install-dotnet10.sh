#!/usr/bin/env bash
set -e

DOTNET_DIR="$HOME/.dotnet"
mkdir -p "$DOTNET_DIR"

curl -fsSL https://dot.net/v1/dotnet-install.sh \
  -o /tmp/dotnet-install.sh

bash /tmp/dotnet-install.sh \
  --channel 10.0 \
  --install-dir "$DOTNET_DIR"

export DOTNET_ROOT="$DOTNET_DIR"
export PATH="$DOTNET_DIR:$PATH"

dotnet --info
