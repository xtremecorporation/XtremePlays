# X-TREME PLAY'S

**Your Music. Your Space. Your Play.**

X-TREME PLAY'S is a Windows music companion with an embedded WebView2 browser and an optional voice/music mixer for a virtual audio cable. YouTube, YouTube Music, Spotify, accounts, and playback are provided by each website and remain subject to that service's own rules.

## Install

Download **`XtremePlays-<version>-Full-Setup.exe`** from the repository's [GitHub Releases](https://github.com/xtremecorporation/XtremePlays/releases). This integrated setup includes the app and the unchanged official VB-CABLE package. VB-CABLE installation is selectable; administrator approval is requested only when the driver needs installation or an older system-wide X-TREME PLAY'S installation must be migrated. The app still installs if VB-CABLE is skipped or needs a Windows restart.

The app is installed for the current Windows account under `%LOCALAPPDATA%\XtremePlays`, with Start menu and optional desktop shortcuts. Windows lists it in **Settings → Apps → Installed apps**, where it can be uninstalled normally. Uninstalling the app keeps its local settings/browser data and leaves VB-CABLE installed because other apps may use that system-wide driver. The setup also installs Microsoft's WebView2 Runtime from Microsoft if it is missing. Internet is needed for that runtime step and for update checks. Setup can launch the app when installation finishes.

The app checks for a stable GitHub update every time it opens. If one is available, it downloads and applies it automatically, then reopens. App updates do not reinstall VB-CABLE and do not require admin approval. If a check cannot reach GitHub, the app continues to work and tries again the next time it opens.

Only one X-TREME PLAY'S window runs per Windows user session. Opening the app again brings the existing window forward.

Users of version 1.2.2 or earlier must run the new full setup once to move from the old Program Files installation to the updater-managed per-user installation. Their bookmarks, settings, WebView2 sessions, and VB-CABLE installation are preserved. After that one-time move, later app updates are automatic.

The separate Velopack `XtremePlays-win-Setup.exe` release asset installs or updates only the app. Use the **Full Setup** asset when VB-CABLE is needed or when migrating from version 1.2.2 or earlier.

## Build locally

Requirements: Windows 10 version 2004 (build 19041) or later, x64, the .NET 10 SDK, and Rust stable with the `x86_64-pc-windows-msvc` target (`rustup` from [rustup.rs](https://rustup.rs/)).

Build the Brave ad-block engine and self-contained app payload, then create the optional Velopack installer:

```powershell
./packaging/build-adblock.ps1
./packaging/publish-velopack.ps1
dotnet tool install --global vpk --version 1.2.161
vpk pack --packId XtremePlays --packVersion 1.4.0 --runtime win-x64 --packDir .installer-staging/velopack-app --mainExe XtremePlays.exe --packTitle "X-TREME PLAY'S" --packAuthors "X-TREME CORPORATION" --icon src/XtremePlays.App/Assets/XtremePlays.ico --splashImage src/XtremePlays.App/Assets/BrandLogo.png --shortcuts StartMenuRoot,Desktop --noPortable --outputDir artifacts/velopack
./packaging/build-installer.ps1
```

The integrated setup is written to `artifacts/XtremePlays-1.4.0-Full-Setup.exe`. `./packaging/publish.ps1` separately creates the portable ZIP; portable builds do not self-update and store data beside the executable.

## Publish an update with GitHub Actions

The `Windows build` workflow builds app changes on pushes and pull requests to `main`. The `Build and publish Windows release` workflow runs only for a version tag such as `v1.3.4`. Before tagging a release:

1. Update the app and setup `<Version>` values to the same `x.y.z` version.
2. Add a matching section at the top of `CHANGELOG.md`.
3. Push the version tag, for example `git tag v1.4.0` followed by `git push origin v1.4.0`.

GitHub Actions builds the app, creates Velopack full/delta packages, builds the integrated driver setup, uploads all release assets to a draft, and publishes only after the full setup is attached. No personal access token or signing secret is required; the workflow uses its repository-scoped `GITHUB_TOKEN`. The repository must be public for anonymous clients to receive updates.

The first updater-enabled release is a migration release: existing 1.2.2 installations cannot self-update because those builds do not contain the updater. They need the full setup once. Later releases update through Velopack without rerunning the driver installer.

## Features

- Native resizable WPF window with light and dark themes.
- Embedded WebView2 browser with navigation, search, loading/error status, and same-window popup handling.
- YouTube, YouTube Music, and Spotify shortcuts, plus custom bookmark add/edit/remove/reorder.
- Versioned JSON backup and restore for the complete bookmark collection.
- Persistent WebView2 profile for provider sessions. Navigating to Settings, Audio Sharing, About, Bookmarks, or Home keeps the current service page alive.
- Explicitly started WASAPI mixing of this app's browser audio and a selected microphone to a selected virtual playback endpoint.
- Separate voice, music, and master gains; live level meters; quick mute buttons; configurable global keyboard shortcuts.
- Portable mode and a self-contained x64 app build.
- Integrated setup that bundles VB-CABLE and migrates the previous system-wide app install without deleting app data.
- GitHub Releases updates, Windows build/release automation, and weekly Dependabot checks for NuGet and workflow actions.
- Optional Brave adblock-rust network filtering for the embedded browser, controlled in Settings and backed by the user's local EasyList/EasyPrivacy cache.

## Developer

X-TREME PLAY'S is developed by **Abu Rayhan**. Find him on [Facebook](https://www.facebook.com/imaburayhan10x), visit [aburayhan.pro.bd](https://aburayhan.pro.bd/), or see [X-TREME CORPORATION on GitHub](https://github.com/xtremecorporation).

## Audio and service limitations

- The app does not record audio or send it to an X-TREME server. Audio capture starts only when the user presses Start sharing; it stops when sharing is stopped or the app closes. The mixed stream goes only to the selected local virtual playback endpoint.
- To route the mix into Discord or WhatsApp, choose the matching VB-CABLE recording endpoint as that call app's microphone and enable its own call/share feature. Actual call quality depends on the Windows device drivers and the destination app.
- Music playback, provider sign-in, account features, and player controls belong to the websites. App-level play/pause, track metadata, media keys, and player volume are not exposed consistently across providers.
- The mixer applies voice/music/master gains and a peak limiter. Headphones help reduce room echo; keep levels below clipping.
- A successful Windows build does not certify a clean PC install, microphone/cable compatibility, or every provider's behavior. Test those on the target Windows and call-app setup.

## Ad-blocking limitations

- Ad blocking is optional and off by default. When enabled, the Brave adblock-rust network filter engine evaluates WebView2 requests locally against EasyList and EasyPrivacy. The app downloads those public filter lists from their official sources and refreshes them about once a day while the feature is in use; it does not send browsing URLs to an X-TREME PLAY'S server.
- The app includes network request filtering, not Brave Browser or the complete Brave Shields implementation. Some providers deliver ads from the same hosts and media streams as the requested content, and some ad placements are part of the page itself. Those ads may still appear. Cosmetic hiding and every rule that requires browser-specific script or resource replacement are not guaranteed. Websites can also change or detect filtering, so this feature cannot promise 100% ad removal.
- A page may break when a required request is blocked. Turn the setting off and reload that page to restore normal requests. When enabling the feature on a page that is already open, reload it if you want newly loaded content to use the rules; current playback is not stopped automatically.

## VB-CABLE and third-party terms

VB-CABLE is a separate VB-Audio donationware product. The release bundle includes only the unchanged base driver package, identifies VB-Audio as the source, and links to the vendor's [product page](https://vb-cable.com/) and [licensing/donation terms](https://vb-audio.com/Services/licensing.htm). Professional or commercial distribution may require additional VB-Audio licenses. The driver remains installed if X-TREME PLAY'S is uninstalled because other apps may use it. See [`packaging/third-party/VB-CABLE-NOTICE.txt`](packaging/third-party/VB-CABLE-NOTICE.txt).

The Brave adblock-rust library is MPL-2.0 licensed; the app build pins version 0.13.5 and includes the upstream source reference and license in [`packaging/third-party/BRAVE-ADBLOCK-NOTICE.txt`](packaging/third-party/BRAVE-ADBLOCK-NOTICE.txt) and [`packaging/third-party/MOZILLA-PUBLIC-LICENSE-2.0.txt`](packaging/third-party/MOZILLA-PUBLIC-LICENSE-2.0.txt). EasyList and EasyPrivacy are fetched at runtime after opt-in and are not redistributed in the installer. Their maintainers publish their own license terms; see [`packaging/third-party/EASYLIST-FILTER-LISTS-NOTICE.txt`](packaging/third-party/EASYLIST-FILTER-LISTS-NOTICE.txt).

## Privacy and app data

No X-TREME account, telemetry, or background service is used. Browser cookies and website storage remain in `%LOCALAPPDATA%\XtremeCorporation\XtremePlays\BrowserData`; app preferences and bookmarks are stored in the same local data folder. Resetting app preferences keeps website sessions; clearing browser data removes them after confirmation. App updates are checked through the public GitHub Releases feed; they do not send browser, account, audio, or preference data.

## License

The X-TREME PLAY'S source code is MIT licensed; see [`LICENSE`](LICENSE). The bundled VB-CABLE archive is a separate VB-Audio product under its vendor's terms and is **not** relicensed under MIT. Microsoft WebView2 and NAudio retain their own package/runtime terms.
