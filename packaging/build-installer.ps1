param(
    [Parameter(Mandatory = $false)]
    [string]$AppSetupPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$setupProject = Join-Path $repoRoot 'src\XtremePlays.Setup\XtremePlays.Setup.csproj'
$driverPackage = Join-Path $PSScriptRoot 'third-party\VBCABLE_Driver_Pack45.zip'
$noticeFile = Join-Path $PSScriptRoot 'third-party\VB-CABLE-NOTICE.txt'
$artifactDir = Join-Path $repoRoot 'artifacts'
$project = Join-Path $repoRoot 'src\XtremePlays.App\XtremePlays.App.csproj'
$projectMetadata = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$projectMetadata.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Set a valid semantic version in the app project before packaging.' }

if ([string]::IsNullOrWhiteSpace($AppSetupPath)) {
    $AppSetupPath = Join-Path $artifactDir 'velopack\XtremePlays-win-Setup.exe'
}
$AppSetupPath = [IO.Path]::GetFullPath($AppSetupPath)
if (-not (Test-Path -LiteralPath $AppSetupPath -PathType Leaf)) {
    throw "The Velopack installer is missing: $AppSetupPath. Run publish-velopack.ps1, then vpk pack, or pass -AppSetupPath."
}

if (-not (Test-Path -LiteralPath $driverPackage)) { throw 'The official VB-CABLE package is missing.' }
if (-not (Test-Path -LiteralPath $noticeFile)) { throw 'The VB-CABLE distribution notice is missing.' }
$expectedDriverHash = 'B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB'
$actualDriverHash = (Get-FileHash -LiteralPath $driverPackage -Algorithm SHA256).Hash
if ($actualDriverHash -ne $expectedDriverHash) { throw "The bundled VB-CABLE archive hash changed. Expected $expectedDriverHash; found $actualDriverHash." }

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:APPDATA = Join-Path $repoRoot '.appdata'
$env:LOCALAPPDATA = Join-Path $repoRoot '.localappdata'

$resourceProperties = @(
    "-p:AppSetupPath=$AppSetupPath",
    "-p:DriverPackagePath=$driverPackage",
    "-p:NoticePath=$noticeFile"
)
$restoreArguments = @('restore', $setupProject, '--configfile', (Join-Path $repoRoot 'NuGet.Config'), '-r', 'win-x64', '--ignore-failed-sources') + $resourceProperties
& dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Setup project restore failed.' }

$setupPublishDir = Join-Path $repoRoot '.installer-staging\setup-publish'
$publishArguments = @('publish', $setupProject, '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '--no-restore', '-o', $setupPublishDir) + $resourceProperties
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Setup executable publish failed.' }

$builtSetup = Join-Path $setupPublishDir 'XtremePlaysSetup.exe'
if (-not (Test-Path -LiteralPath $builtSetup)) { throw 'The setup publish completed without the expected executable.' }
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
$payloadOutput = Join-Path $artifactDir "XtremePlays-$version-Full-Setup.exe"
Copy-Item -LiteralPath $builtSetup -Destination $payloadOutput -Force
$signature = Get-AuthenticodeSignature -LiteralPath $payloadOutput
if ($signature.Status -ne 'NotSigned') { throw "Unexpected setup signature state: $($signature.Status)." }
$file = Get-Item -LiteralPath $payloadOutput
Write-Host "Integrated Windows setup created: $($file.FullName)"
Write-Host "Size: $([Math]::Round($file.Length / 1MB, 1)) MB"
Write-Host 'The setup is unsigned. Windows SmartScreen may show a warning.'
