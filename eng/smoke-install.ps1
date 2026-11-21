# Windows equivalent of smoke-install.sh.
$ErrorActionPreference = 'Stop'
$here = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not (Test-Path (Join-Path $here 'artifacts/sdk-feed'))) { throw 'Run eng/build-sdk-feed.ps1 first.' }
$pkg = Join-Path $here 'artifacts/pkg'
if (Test-Path $pkg) { Remove-Item -Recurse -Force $pkg }
New-Item -ItemType Directory -Force $pkg | Out-Null
dotnet pack (Join-Path $here 'src/Koras.Connect.Cli/Koras.Connect.Cli.csproj') -c Release -o $pkg
if ($LASTEXITCODE -ne 0) { throw 'pack failed' }
$work = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
  $version = ([xml](Get-Content (Join-Path $here 'src/Koras.Connect.Cli/Koras.Connect.Cli.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
  @"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear />
<add key="cli-pkg" value="$pkg" />
<add key="sdk-feed" value="$(Join-Path $here 'artifacts/sdk-feed')" />
<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
</packageSources></configuration>
"@ | Set-Content (Join-Path $work 'nuget.config')
  Push-Location $work
  dotnet tool install Koras.Connect.Cli --version $version --tool-path (Join-Path $work 'tools') --configfile (Join-Path $work 'nuget.config')
  if ($LASTEXITCODE -ne 0) { throw 'tool install failed' }
  $tool = Join-Path $work 'tools/koras-connect.exe'
  $env:KORAS_CONNECT_HOME = Join-Path $work 'home'; $env:KORAS_SMOKE_SECRET = 'smoke-secret-value'
  & $tool --version; if ($LASTEXITCODE -ne 0) { throw '--version failed' }
  & $tool --help | Select-Object -First 5
  & $tool profile init smoke --connector qualtrics --set apiRoot=https://dc.example.test/API/v3 --credential apiToken=env:KORAS_SMOKE_SECRET | Out-Null
  & $tool profile list
  & $tool profile validate smoke; if ($LASTEXITCODE -ne 0) { throw 'validate failed' }
  $out = (& $tool profile validate smoke 2>&1) -join "`n"
  if ($out -match 'smoke-secret-value') { throw 'secret leaked' }
  & $tool nonsense 2>$null; if ($LASTEXITCODE -ne 2) { throw "expected exit 2, got $LASTEXITCODE" }
  Write-Host "SMOKE OK ($version)"
} finally { Pop-Location; Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
