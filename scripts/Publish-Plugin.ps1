param(
    [Parameter(Mandatory)][string]$ExtensionFolder,
    [Parameter(Mandatory)][string]$PluginId,
    [Parameter(Mandatory)][string]$ExtensionId,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Description,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$SourceUrl,
    [Parameter(Mandatory)][string]$License,
    [Parameter(Mandatory)][string]$ReleaseTag,
    [string]$LicenseFile,
    [string]$NoticeFile,
    [string]$Category = 'Extensions'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$extensionRoot = [IO.Path]::GetFullPath($ExtensionFolder)
if (-not (Test-Path -LiteralPath $extensionRoot -PathType Container)) { throw 'ExtensionFolder must point to the unpacked extension folder.' }
if ($PluginId -notmatch '^[A-Za-z0-9_-]{1,80}$') { throw 'PluginId can contain only letters, numbers, dashes, and underscores.' }
if ($Version -notmatch '^[A-Za-z0-9._-]{1,40}$') { throw 'Version has unsupported characters.' }
if ($ExtensionId -notmatch '^[a-p]{32}$') { throw 'ExtensionId must be the 32-character Chrome extension ID.' }
if ($Category -notin @('Extensions', 'Ad blockers', 'Music', 'Tools')) { throw 'Choose a supported catalog category.' }
if ($SourceUrl -notmatch '^https://') { throw 'SourceUrl must be HTTPS.' }

$manifestPath = Join-Path $extensionRoot 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'The unpacked extension folder must contain manifest.json at its root.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.manifest_version -notin @(2, 3)) { throw 'Only Manifest V2 and V3 packages are supported.' }
if ([string]::IsNullOrWhiteSpace([string]$manifest.name)) { throw 'The extension manifest has no display name.' }
if ($manifest.version -ne $Version) { throw "Catalog version $Version does not match manifest version $($manifest.version)." }

$queue = [Collections.Generic.Queue[string]]::new()
$queue.Enqueue($extensionRoot)
$fileCount = 0
$totalBytes = 0L
while ($queue.Count -gt 0) {
    $directory = $queue.Dequeue()
    foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
        $attributes = [IO.File]::GetAttributes($path)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The extension folder contains a symbolic link or reparse point.' }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $queue.Enqueue($path); continue }
        $fileCount++
        $totalBytes += ([IO.FileInfo]$path).Length
        if ($fileCount -gt 5000 -or $totalBytes -gt 150MB) { throw 'The extension exceeds the 5,000-file or 150 MB publishing limit.' }
    }
}

if ($LicenseFile -and -not (Test-Path -LiteralPath $LicenseFile -PathType Leaf)) { throw 'LicenseFile does not exist.' }
if ($NoticeFile -and -not (Test-Path -LiteralPath $NoticeFile -PathType Leaf)) { throw 'NoticeFile does not exist.' }
if ($License -ne 'MIT' -and -not $LicenseFile -and -not $NoticeFile) {
    throw 'Include a LicenseFile or NoticeFile for third-party extensions before publishing.'
}

$assetName = "XtremePlays-Plugin-$PluginId-$Version.zip"
$outputDir = Join-Path $repoRoot 'artifacts\plugins'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$assetPath = Join-Path $outputDir $assetName
$tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
$stagePath = [IO.Path]::GetFullPath((Join-Path $env:TEMP "XtremePluginPublish-$([guid]::NewGuid().ToString('N'))"))
if (-not $stagePath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The temporary staging folder is outside the Windows temporary directory.' }

try {
    New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($extensionRoot)) {
        Copy-Item -LiteralPath $entry -Destination $stagePath -Recurse -Force
    }
    if ($LicenseFile) { Copy-Item -LiteralPath $LicenseFile -Destination (Join-Path $stagePath 'THIRD-PARTY-LICENSE.txt') -Force }
    if ($NoticeFile) { Copy-Item -LiteralPath $NoticeFile -Destination (Join-Path $stagePath 'THIRD-PARTY-NOTICE.txt') -Force }
    if (Test-Path -LiteralPath $assetPath) { Remove-Item -LiteralPath $assetPath -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($stagePath, $assetPath, [IO.Compression.CompressionLevel]::Optimal, $false)

    gh release view $ReleaseTag --repo 'xtremecorporation/XtremePlays' *> $null
    if ($LASTEXITCODE -ne 0) { throw "GitHub release $ReleaseTag does not exist. Use an app release tag so plugin-only tags do not affect automatic app updates." }
    gh release upload $ReleaseTag $assetPath --repo 'xtremecorporation/XtremePlays' --clobber
    if ($LASTEXITCODE -ne 0) { throw 'The plugin package was built but GitHub could not upload it. Check gh auth status and try again.' }

    $checksum = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash
    Write-Host "Uploaded $assetName to release $ReleaseTag"
    Write-Host "SHA-256: $checksum"
    Write-Host 'Add this entry to plugins/catalog.json, review it, and merge the catalog change to main:'
    [ordered]@{
        id = $PluginId
        name = [string]$manifest.name
        description = $Description
        category = $Category
        type = 'browser-extension'
        version = $Version
        publisher = $Publisher
        sourceLabel = 'X-TREME curated'
        sourceUrl = $SourceUrl
        releaseTag = $ReleaseTag
        assetName = $assetName
        sha256 = $checksum
        license = $License
        extensionId = $ExtensionId
        capabilities = @('Browser extension')
    } | ConvertTo-Json -Depth 5
}
finally {
    if ($stagePath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $stagePath)) {
        Remove-Item -LiteralPath $stagePath -Recurse -Force
    }
}
