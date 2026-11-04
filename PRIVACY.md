# X-TREME PLAY'S Privacy Notice

Last updated: 5 November 2026

X-TREME PLAY'S is a Windows desktop music companion. It does not require an X-TREME account and does not operate an X-TREME backend, analytics service, or telemetry service.

## Music websites and saved links

YouTube, YouTube Music, Spotify, and other websites handle their own sign-in, playback, accounts, and recommendations. Users sign in on the provider's website inside the app's persistent WebView2 browser profile. X-TREME PLAY'S does not receive provider passwords, call Spotify or Google music-library APIs, or read/import a provider's library.

The optional Home library contains only links the user chooses to add. Links, titles, and bookmarks are stored in the app's local settings file under the current Windows account's app-data folder. Users can edit, remove, export, or restore them as JSON. Opening a saved item navigates to its original website.

## Browser, audio, plugins, and updates

Websites opened in the embedded browser store their cookies and website data in the local WebView2 profile. Use Settings to clear that browser profile; this signs the user out of websites. The microphone/audio mixer processes audio locally and starts only when the user explicitly starts audio sharing. It sends the mix to the selected local Windows audio endpoint, not to an X-TREME server.

The app checks public GitHub Releases for updates. The Plugins window reads the public X-TREME catalog and contacts GitHub only when loading it or downloading a selected plugin. Optional ad filtering retrieves filter lists from their published sources when enabled.

## Upgrade cleanup and local deletion

If an earlier preview of the app stored music API tokens or a synced metadata cache, this version imports valid cached music links into the local saved-link library and removes the obsolete tokens. A cache that is too large or has more links than currently fit in the bookmark collection is kept until it can be safely imported. Website sign-ins in the WebView2 profile are separate and are preserved.

App settings, saved links, and the WebView2 profile live under the current Windows user's local app-data folder. Clearing browser data removes website cookies and site storage. Uninstalling the app keeps local app data so a later reinstall can preserve the profile; delete the local X-TREME PLAY'S data folder yourself if you want to remove it completely.

## Contact

For privacy questions, contact the developer through [aburayhan.pro.bd](https://aburayhan.pro.bd/) or the [X-TREME CORPORATION GitHub organization](https://github.com/xtremecorporation).
