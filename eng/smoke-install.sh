#!/usr/bin/env bash
# Packs the tool, installs it into an empty tool path from local feeds only (plus nuget.org for third-party
# dependencies), and exercises the INSTALLED command. Requires artifacts/sdk-feed (eng/build-sdk-feed.sh).
set -euo pipefail
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
here="$(cd "$(dirname "$0")/.." && pwd)"
[ -d "$here/artifacts/sdk-feed" ] || { echo "Run eng/build-sdk-feed.sh first." >&2; exit 2; }
rm -rf "$here/artifacts/pkg"; mkdir -p "$here/artifacts/pkg"
dotnet pack "$here/src/Koras.Connect.Cli/Koras.Connect.Cli.csproj" -c Release -o "$here/artifacts/pkg"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
version="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$here/src/Koras.Connect.Cli/Koras.Connect.Cli.csproj" | head -1)"
cat > "$work/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear />
<add key="cli-pkg" value="$here/artifacts/pkg" />
<add key="sdk-feed" value="$here/artifacts/sdk-feed" />
<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
</packageSources></configuration>
EOF
cd "$work"
dotnet tool install Koras.Connect.Cli --version "$version" --tool-path "$work/tools" --configfile "$work/nuget.config"
tool="$work/tools/koras-connect"
export KORAS_CONNECT_HOME="$work/home" KORAS_SMOKE_SECRET="smoke-secret-value"
echo "--- --version";  "$tool" --version
echo "--- --help";     "$tool" --help | head -5
echo "--- profile init/list/validate"
"$tool" profile init smoke --connector qualtrics --set apiRoot=https://dc.example.test/API/v3 --credential apiToken=env:KORAS_SMOKE_SECRET >/dev/null
"$tool" profile list
"$tool" profile validate smoke
echo "--- secrets never printed"
if "$tool" profile validate smoke 2>&1 | grep -q "smoke-secret-value"; then echo "LEAK" >&2; exit 1; fi
echo "--- usage error exit code"
set +e; "$tool" nonsense 2>/dev/null; code=$?; set -e; [ "$code" = 2 ] || { echo "expected exit 2, got $code" >&2; exit 1; }
echo "SMOKE OK ($version)"
