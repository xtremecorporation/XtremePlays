# X-TREME PLAY'S Privacy Notice

Last updated: 5 November 2026

X-TREME PLAY'S is a Windows desktop music companion. It does not require an X-TREME account and does not operate an X-TREME backend, analytics service, or telemetry service.

## Music account connections

Music account connections are optional. When you choose **Connect Spotify** or **Connect YouTube**, the app opens the provider's sign-in page in your normal system browser. The provider asks you to approve the requested read-only access. X-TREME PLAY'S does not ask for, store, or receive your provider password.

- **Spotify:** with your consent, the app reads saved-track metadata (title, artists, and artwork URL) and links each item back to Spotify. That metadata is held in app memory and is cleared when the app closes. The app does not retrieve or download audio.
- **YouTube:** with your consent, the app reads your playlists and their video metadata and links each item back to YouTube. A local metadata cache is kept in your Windows profile so Home can show it between launches. Disconnect YouTube in Settings to delete the cached list.
- **OAuth tokens:** refresh/access tokens are protected with Windows Data Protection for the current Windows account and stored locally. Disconnect the provider in Settings to delete its token. Provider account sessions in your system browser or in-app WebView2 profile are separate and are not cleared by disconnecting API sync.
- **Requests:** provider API requests go directly from the app to Spotify or Google. X-TREME PLAY'S does not receive your library data. Provider terms, privacy policies, availability, and consent controls continue to apply.

YouTube Data API does not provide a general YouTube Music personal-library/recommendation feed through this integration. Spotify development access also has provider-imposed limits until Spotify approves wider access.

## Browser, audio, plugins, and updates

Websites opened in the embedded browser load and store their own cookies and website data in the local WebView2 profile. Use Settings to clear that browser profile; this does not remove music API tokens. The microphone/audio mixer processes audio locally and starts only when you explicitly start audio sharing. It sends the mix to the selected local Windows audio endpoint, not to an X-TREME server.

The app checks public GitHub Releases for updates. The Plugins window reads the public X-TREME catalog and contacts GitHub only when loading it or downloading a plugin you selected. Optional ad filtering retrieves filter lists from their published sources when you enable it.

## Local storage and deletion

App settings, bookmarks, YouTube playlist metadata, provider tokens, and the WebView2 profile live under your Windows local app-data folder. Disconnect each music provider to remove its API token and synced metadata. Clearing browser data removes the WebView2 cookies and site storage. Uninstalling the app keeps local app data so a later reinstall can preserve your profile; delete the local X-TREME PLAY'S data folder yourself if you want to remove it completely.

## Contact

For privacy questions, contact the developer through [aburayhan.pro.bd](https://aburayhan.pro.bd/) or the [X-TREME CORPORATION GitHub organization](https://github.com/xtremecorporation).
