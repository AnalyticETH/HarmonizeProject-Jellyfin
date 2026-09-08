# Jellyfin Philips Hue Sync Plugin

Synchronize Philips Hue lights with video and audio playing on your Jellyfin
server. The plugin uses FFmpeg to analyze media and streams lighting updates to
a Hue Entertainment Area.

See [Release readiness](RELEASE_READINESS.md) for the release checklist and
physical-acceptance status.

## What it does

- Matches lighting to video colors or audio-reactive effects.
- Supports different users, playback devices, bridges, and rooms.
- Runs saved scenes, playlists, and scheduled lighting cues.
- Offers pause behavior, original-light-state restoration, and diagnostics.

Detailed controls are in the [Configuration reference](docs/CONFIGURATION.md).

## Requirements

| Component | Requirement |
| --- | --- |
| Jellyfin | Server 10.9.0 or newer; administrator access for setup |
| Hue bridge | V2 (square), reachable from the Jellyfin server |
| Lights | Color-capable Hue lights in an Entertainment Area configured in the Hue app |
| FFmpeg | Available to Jellyfin; its bundled encoder is supported |

The managed DTLS transport is included. You do not need an OpenSSL executable.

## Installation

Download the release ZIP from the
[GitHub releases page](https://github.com/AnalyticETH/HarmonizeProject-Jellyfin/releases).
Use the ZIP for the version you want to install.

### Install the files

Find the Jellyfin plugins directory for your installation:

| Platform | Plugins directory |
| --- | --- |
| Linux | `/var/lib/jellyfin/plugins` |
| Windows | `%ProgramData%\Jellyfin\Server\plugins` |
| Docker | `/config/plugins` inside the container |

1. Read the four-part version from the release's `meta.json` inside the ZIP.
2. Create a `HueSync_<version>` folder under the plugins directory, replacing
   `<version>` with that exact value.
3. Extract the ZIP directly into that folder. The package contains:

   ```text
   BouncyCastle.Cryptography.dll
   Jellyfin.Plugin.Hue.dll
   LICENSE
   NOTICE
   meta.json
   ```

4. Restart Jellyfin and open **Dashboard -> Plugins -> Philips Hue Sync**.

Do not install just the plugin DLL or add an extra directory level inside the
versioned folder. See the [Support guide](SUPPORT.md) if the plugin does not load.

## Initial setup

1. Open the plugin configuration and use **Discover Bridge**, or enter its
   private/local address.
2. Select **Verify/Trust Certificate**. Verify the SHA-256 fingerprint against
   your trusted bridge identity and explicitly confirm it before sending keys.
   Do not approve an unexpected certificate change without investigating it.
3. Press the physical **Link Button** on the bridge, then select **Link Bridge**.
4. Select **Refresh Entertainment Areas** and choose the area you configured
   in the Hue app.
5. Choose your media-sync settings, save, and run **Test Connection** and
   **System Diagnostics** before starting playback.

Use the [Configuration reference](docs/CONFIGURATION.md) for per-user/device
mapping, scenes, schedules, and manual credential setup. Keep App Keys and
Client Keys private; do not include them in issues or shared logs.

## Documentation and help

| Guide | Contents |
| --- | --- |
| [Configuration reference](docs/CONFIGURATION.md) | Settings, targeting, automation, and bridge credentials |
| [Administrator API](docs/API.md) | Authenticated endpoints, request examples, and safety contracts |
| [Contributing guide](CONTRIBUTING.md) | Building, tests, benchmarks, and release packaging |
| [Support guide](SUPPORT.md) | Troubleshooting and safe bug reports |
| [Changelog](CHANGELOG.md) | Release history |
| [Release readiness](RELEASE_READINESS.md) | Production-release evidence and outstanding gates |

Use the [Issue templates](.github/ISSUE_TEMPLATE/) for bugs and feature requests.
Report vulnerabilities privately through the [Security policy](SECURITY.md).
Participation follows the [Code of Conduct](CODE_OF_CONDUCT.md).

## License

Licensed under the [GNU GPLv3 (GPL-3.0-only)](LICENSE).
Project copyright and bundled dependency notices are in [NOTICE](NOTICE).
