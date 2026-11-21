#!/usr/bin/env bash
# Builds the pinned Koras Connect SDK into a local NuGet feed at artifacts/sdk-feed.
# Usage: eng/build-sdk-feed.sh [path-to-koras-connect-sdk-checkout]
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${1:-$here/../koras-connect-sdk}"
pin="$(tr -d '[:space:]' < "$here/SDK-COMMIT")"
[ -d "$sdk/.git" ] || { echo "SDK checkout not found at $sdk (git clone https://github.com/kkora/koras-connect-sdk)" >&2; exit 2; }
actual="$(git -C "$sdk" rev-parse HEAD)"
if [ "$actual" != "$pin" ]; then
  echo "SDK checkout is at $actual but SDK-COMMIT pins $pin. Run: git -C '$sdk' checkout $pin" >&2; exit 2
fi
rm -rf "$here/artifacts/sdk-feed"; mkdir -p "$here/artifacts/sdk-feed"
dotnet pack "$sdk/Koras.Connect.slnx" -c Release -o "$here/artifacts/sdk-feed" -p:ContinuousIntegrationBuild=true
ls "$here/artifacts/sdk-feed"
