$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\XtremePlays.App\XtremePlays.App.csproj'
$artifactDir = Join-Path $repoRoot 'artifacts'
$projectMetadata = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$projectMetadata.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Set a valid semantic version in the project file before publishing.' }
$packageName = "XtremePlays-$version-win-x64"
$publishDir = Join-Path $artifactDir $packageName
$stagingDir = Join-Path $env:TEMP "XtremePlays-publish-$PID"
if ($stagingDir.Contains("'")) { throw 'The temporary publish path must not contain an apostrophe. Set TEMP to a writable path without one and retry.' }

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:APPDATA = Join-Path $repoRoot '.appdata'
$env:LOCALAPPDATA = Join-Path $repoRoot '.localappdata'

& (Join-Path $PSScriptRoot 'build-adblock.ps1')

dotnet restore $project --configfile (Join-Path $repoRoot 'NuGet.Config') -r win-x64 --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }

dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --no-restore -o $stagingDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'portable.flag') -Destination (Join-Path $stagingDir 'portable.flag') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $stagingDir 'README.txt') -Force
$thirdPartyDir = Join-Path $stagingDir 'ThirdParty'
New-Item -ItemType Directory -Path $thirdPartyDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third-party\BRAVE-ADBLOCK-NOTICE.txt') -Destination (Join-Path $thirdPartyDir 'BRAVE-ADBLOCK-NOTICE.txt') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third-party\MOZILLA-PUBLIC-LICENSE-2.0.txt') -Destination (Join-Path $thirdPartyDir 'MOZILLA-PUBLIC-LICENSE-2.0.txt') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third-party\EASYLIST-FILTER-LISTS-NOTICE.txt') -Destination (Join-Path $thirdPartyDir 'EASYLIST-FILTER-LISTS-NOTICE.txt') -Force

New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
Get-ChildItem -LiteralPath $stagingDir | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $publishDir -Recurse -Force }

$exePath = Join-Path $publishDir 'XtremePlays.exe'
if (-not (Test-Path -LiteralPath $exePath)) { throw 'The publish completed without the expected executable.' }
$zipPath = Join-Path $artifactDir "$packageName.zip"
Compress-Archive -Path (Join-Path $stagingDir '*') -DestinationPath $zipPath -Force
Write-Host "Portable app published: $exePath"
Write-Host "Portable archive created: $zipPath"
