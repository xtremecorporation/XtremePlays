# Changelog

## 1.3.0

- Add automatic stable update checks and background downloads from GitHub Releases; updates apply on the next app launch.
- Move managed installs to the current Windows account so future app updates do not require administrator permission.
- Add an integrated first-run setup that bundles VB-CABLE and migrates the previous Program Files installation while preserving app data.
- Add Windows build and release automation, dependency update management, contribution guidance, and the MIT source license.

## 1.2.2

- Keep the current YouTube, YouTube Music, or Spotify page alive when returning from Settings, About, Audio Sharing, Bookmarks, or Home.
- Add a versioned JSON export and restore flow for the complete bookmark collection.
- Add quick microphone and music mute toggles for the virtual call output, plus configurable global keyboard shortcuts.

## 1.2.1

- Fixed setup's VB-CABLE installed check to look for the actual `VBAudioVACMME` service declared by the bundled driver INF.
- Detect the registered driver service through Windows' service registry entry, so a valid installed driver is not rejected by a mismatched service name.

## 1.2.0

- Added a single native Windows setup EXE that installs the app and bundled base VB-CABLE driver.
- Added Program Files installation, Start Menu and desktop shortcuts, and an Add/Remove Programs uninstall entry.
- Added the VB-Audio donationware origin and license notice to the setup and installed files.
- Kept the driver's own installation and possible restart requirements visible to users.

## 1.1.1

- Prefer the Realtek microphone on first launch when no saved microphone is available.
- Save the user's explicit microphone selection and default it for subsequent launches.

## 1.1.0

- Added explicit microphone + app-browser audio mixing to a virtual playback endpoint.
- Added separate voice, music, and master output gain sliders with live level meters.
- Saved selected microphone, virtual output, and mixer levels locally.
- Added start/stop capture controls and virtual cable setup guidance.

## 1.0.1

- Added an explicit OK button to save an active Capture microphone choice and restore it on the next visit.
- Replaced the placeholder play mark with the supplied X-TREME PLAY'S logo in the app branding and Windows icon.
- Clarified that saving a microphone selection does not start capture or audio routing.

## 1.0.0

- Added the native X-TREME PLAY'S WPF shell and branded light/dark themes.
- Added WebView2 navigation, persistent browser sessions, and bookmark management.
- Added local JSON preferences, privacy controls, compact window mode, and audio endpoint inspection.
- Added a self-contained x64 publish workflow with an optional portable data folder.
- Documented WebView2 and Discord audio-routing dependencies and limitations.
