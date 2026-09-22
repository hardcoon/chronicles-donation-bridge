[CmdletBinding()]
param([string]$DotNet = 'dotnet')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$bridgeRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$bridgeArtifacts = Join-Path $bridgeRoot 'artifacts'
$bridgeSdk = (& $DotNet --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $bridgeSdk -notmatch '^8\.') { throw 'Install the .NET 8 SDK for Windows or pass -DotNet with its dotnet.exe path.' }

& $DotNet build (Join-Path $bridgeRoot 'tests\ChroniclesDonationBridge.Tests\ChroniclesDonationBridge.Tests.csproj') --configuration Release --artifacts-path $bridgeArtifacts
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
& $DotNet (Join-Path $bridgeArtifacts 'bin\ChroniclesDonationBridge.Tests\release\ChroniclesDonationBridge.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw 'Logic tests failed.' }

& $DotNet build (Join-Path $bridgeRoot 'tests\ChroniclesDonationBridge.UiTests\ChroniclesDonationBridge.UiTests.csproj') --configuration Release --artifacts-path $bridgeArtifacts
if ($LASTEXITCODE -ne 0) { throw 'Headless UI test build failed.' }
& $DotNet (Join-Path $bridgeArtifacts 'bin\ChroniclesDonationBridge.UiTests\release_win-x64\ChroniclesDonationBridge.UiTests.dll') (Join-Path $bridgeArtifacts 'ui-checks')
if ($LASTEXITCODE -ne 0) { throw 'Headless UI tests failed.' }
