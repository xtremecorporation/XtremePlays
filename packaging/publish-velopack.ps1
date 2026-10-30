$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\XtremePlays.App\XtremePlays.App.csproj'
$publishDir = Join-Path $env:TEMP "XtremePlays-vpk-publish-$PID"
$packageInputDir = Join-Path $repoRoot '.installer-staging\velopack-app'
$noticeFile = Join-Path $PSScriptRoot 'third-party\VB-CABLE-NOTICE.txt'
$adBlockNoticeFile = Join-Path $PSScriptRoot 'third-party\BRAVE-ADBLOCK-NOTICE.txt'
$adBlockLicenseFile = Join-Path $PSScriptRoot 'third-party\MOZILLA-PUBLIC-LICENSE-2.0.txt'
$filterListNoticeFile = Join-Path $PSScriptRoot 'third-party\EASYLIST-FILTER-LISTS-NOTICE.txt'
$projectMetadata = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$projectMetadata.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Set a valid semantic version in the app project before packaging.' }

$publishFullPath = [IO.Path]::GetFullPath($publishDir)
$packageInputFullPath = [IO.Path]::GetFullPath($packageInputDir)
$repoFullPath = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
if ($publishFullPath.Contains("'")) {
    throw 'The temporary publish path must not contain an apostrophe because MSBuild uses it in file transforms.'
}
if (-not $packageInputFullPath.StartsWith($repoFullPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Velopack package input folder must remain inside the project workspace.'
}
if (Test-Path -LiteralPath $publishFullPath) {
    Remove-Item -LiteralPath $publishFullPath -Recurse -Force
}
if (Test-Path -LiteralPath $packageInputFullPath) {
    Remove-Item -LiteralPath $packageInputFullPath -Recurse -Force
}
New-Item -ItemType Directory -Path $publishFullPath -Force | Out-Null

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:APPDATA = Join-Path $repoRoot '.appdata'
$env:LOCALAPPDATA = Join-Path $repoRoot '.localappdata'

& (Join-Path $PSScriptRoot 'build-adblock.ps1')

dotnet restore $project --configfile (Join-Path $repoRoot 'NuGet.Config') -r win-x64 --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }

dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -o $publishFullPath
if ($LASTEXITCODE -ne 0) { throw 'Velopack app publish failed.' }

if (-not (Test-Path -LiteralPath (Join-Path $publishFullPath 'XtremePlays.exe'))) {
    throw 'The Velopack publish completed without the expected executable.'
}
if (Test-Path -LiteralPath (Join-Path $publishFullPath 'portable.flag')) {
    throw 'Portable mode must not be included in the update package because updates replace the current app folder.'
}

$thirdPartyDir = Join-Path $publishFullPath 'ThirdParty'
New-Item -ItemType Directory -Path $thirdPartyDir -Force | Out-Null
Copy-Item -LiteralPath $noticeFile -Destination (Join-Path $thirdPartyDir 'VB-CABLE-NOTICE.txt') -Force
Copy-Item -LiteralPath $adBlockNoticeFile -Destination (Join-Path $thirdPartyDir 'BRAVE-ADBLOCK-NOTICE.txt') -Force
Copy-Item -LiteralPath $adBlockLicenseFile -Destination (Join-Path $thirdPartyDir 'MOZILLA-PUBLIC-LICENSE-2.0.txt') -Force
Copy-Item -LiteralPath $filterListNoticeFile -Destination (Join-Path $thirdPartyDir 'EASYLIST-FILTER-LISTS-NOTICE.txt') -Force
Copy-Item -LiteralPath $filterListNoticeFile -Destination (Join-Path $thirdPartyDir 'EASYLIST-FILTER-LISTS-NOTICE.txt') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $publishFullPath 'README.txt') -Force
New-Item -ItemType Directory -Path $packageInputFullPath -Force | Out-Null
Get-ChildItem -LiteralPath $publishFullPath -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $packageInputFullPath -Recurse -Force
}
Write-Host "Velopack application payload created for version ${version}: $packageInputFullPath"
