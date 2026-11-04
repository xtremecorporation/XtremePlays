# X-TREME Plugin Catalog

The app reads only [`catalog.json`](catalog.json). It does not search the Chrome Web Store. A listing appears as X-TREME curated after a maintainer reviews the source, version, license, extension ID, permissions, and package.

## Publishing a browser extension

1. Review the extension source and its license. Confirm the package's extension ID and declared permissions.
2. Create or select an app release such as `v2.0.0`. Plugin packages are assets on app releases; do not create `plugin-*` releases because the app updater follows GitHub's latest stable release.
3. From an authenticated PowerShell session with GitHub CLI (`gh`) installed, run `scripts/Publish-Plugin.ps1` with the unpacked folder, IDs, version, publisher, source, license, and release tag. Include a license or notice file for third-party extensions.
4. The script checks the extension folder, packages its files, uploads a ZIP asset to the selected app release, calculates SHA-256, and prints a catalog entry.
5. Review and commit the entry to `plugins/catalog.json` on `main`. The app treats the catalog and assets in this repository as the curated source. GitHub Release asset checksums are verified before extraction and install.

Example:

```powershell
./scripts/Publish-Plugin.ps1 `
  -ExtensionFolder 'C:\Extensions\Sample' `
  -PluginId 'sample-tool' `
  -ExtensionId 'abcdefghijklmnopabcdefghijklmnop' `
  -Version '1.2.3' `
  -Description 'A reviewed browser add-on.' `
  -Publisher 'Example Publisher' `
  -SourceUrl 'https://github.com/example/sample' `
  -License 'MIT' `
  -ReleaseTag 'v2.0.0'
```

## Package types and trust

Current app adapters support Chromium-compatible browser extensions and the optional Brave `adblock-rust` network filter. New plugin kinds need an app-side adapter and permission model; the catalog does not execute arbitrary scripts or install arbitrary programs. “X-TREME curated” means a maintainer chose and reviewed that repository entry; it is not a Chrome Web Store or publisher security certification.

The two ad-blocking extensions and Brave filter package are separate release assets. They are not included in the Windows app installer and are downloaded only after a user selects Install. The app checks GitHub's asset SHA-256 before safely unpacking a package.
