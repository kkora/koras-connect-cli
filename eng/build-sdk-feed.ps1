# Builds the pinned Koras Connect SDK into a local NuGet feed at artifacts/sdk-feed.
param([string]$Sdk = (Join-Path $PSScriptRoot '..' '..' 'koras-connect-sdk'))
$ErrorActionPreference = 'Stop'
$here = Resolve-Path (Join-Path $PSScriptRoot '..')
$pin = (Get-Content (Join-Path $here 'SDK-COMMIT') -Raw).Trim()
if (-not (Test-Path (Join-Path $Sdk '.git'))) { throw "SDK checkout not found at $Sdk (git clone https://github.com/kkora/koras-connect-sdk)" }
$actual = (git -C $Sdk rev-parse HEAD).Trim()
if ($actual -ne $pin) { throw "SDK checkout is at $actual but SDK-COMMIT pins $pin. Run: git -C '$Sdk' checkout $pin" }
$feed = Join-Path $here 'artifacts' 'sdk-feed'
if (Test-Path $feed) { Remove-Item -Recurse -Force $feed }
New-Item -ItemType Directory -Force $feed | Out-Null
dotnet pack (Join-Path $Sdk 'Koras.Connect.slnx') -c Release -o $feed -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw 'SDK pack failed' }
