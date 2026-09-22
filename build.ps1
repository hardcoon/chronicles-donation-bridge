[CmdletBinding()]
param([string]$DotNet = 'dotnet')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$bridgeRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$bridgeArtifacts = Join-Path $bridgeRoot 'artifacts'
$bridgeOutput = Join-Path $bridgeArtifacts 'publish'
$bridgeProject = Join-Path $bridgeRoot 'src\ChroniclesDonationBridge.App\ChroniclesDonationBridge.App.csproj'
$bridgeSdk = (& $DotNet --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $bridgeSdk -notmatch '^8\.') { throw 'Install the .NET 8 SDK for Windows or pass -DotNet with its dotnet.exe path.' }

& $DotNet publish $bridgeProject --configuration Release --runtime win-x64 --self-contained true --artifacts-path $bridgeArtifacts --output $bridgeOutput '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:PublishTrimmed=false' '-p:ContinuousIntegrationBuild=true' '-p:Deterministic=true' '-p:DebugSymbols=false' '-p:DebugType=None'
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Ready: $bridgeOutput\ChroniclesDonationBridge.exe"
Write-Host 'The application was not started. Copy game-addon into the game as described in README.md.'
