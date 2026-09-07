# Support

## Before asking for help

1. Confirm that Jellyfin is version 10.9.0 or newer and that the installed
   plugin folder is named `HueSync_<version>`.
2. Verify the release ZIP and manifest sidecars before extracting them.
3. Run **Dashboard -> Plugins -> Philips Hue Sync -> System Diagnostics** and
   review the credential-free result.
4. Check the Jellyfin log for `Hue Sync` and `FFmpeg` entries.
5. Confirm that the bridge is a Hue Bridge V2 with an Entertainment Area and
   that the server can reach its private/local address.

## Questions and bugs

Use the issue templates for reproducible bugs and feature requests. Include:

- plugin version, Jellyfin version, operating system, and deployment type;
- the selected playback scope and target type;
- sanitized diagnostic output and concise reproduction steps; and
- whether the problem survives a restart and the latest release.

Never include Hue App Keys, Client Keys, bridge certificates, access tokens,
private IP details that identify another person, or unredacted server logs.

Security vulnerabilities are not support requests. Follow [SECURITY.md](SECURITY.md)
and use a private channel before sharing technical details.
