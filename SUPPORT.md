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

## Troubleshooting

* **Registration fails:** press the physical Link button immediately before clicking
  **Link Bridge**. Hue registration and all v2 REST requests use the bridge's HTTPS API;
  current bridge firmware no longer supports the old HTTP endpoint. The configured target
  must be a private/local bridge address or a .local mDNS name.
* **Discovery finds no bridge:** the Jellyfin server must be able to reach the local network and
  allow mDNS/Bonjour traffic. Cloud discovery is tried first, then the plugin queries the local
  `_hue._tcp.local` service for private addresses. Link-local IPv6 results may include a `%`
  interface scope; retain that suffix when editing the address manually. If both paths are
  unavailable, use a private IP or `.local` host name manually.
* **No areas are listed:** verify the bridge IP and App Key, then click **Refresh
  Entertainment Areas**. The selected area must contain color-capable lights.
* **Lights stop updating:** run **System Diagnostics** first to confirm that `ffmpeg` and the managed DTLS transport are ready for the Jellyfin
  service account and inspect the Jellyfin server log for `Hue Sync` and `FFmpeg` entries. If a
  frame stream ends or fails, or startup cannot complete, the plugin now rolls back immediately,
  restores saved lights—including color-temperature/mirek mode where applicable—and deactivates the area automatically. If repeated DTLS writes and
  reconnect attempts fail, synchronization also stops instead of leaving the lights frozen,
  restores/deactivates safely, and retains the failure diagnostic in the Live Sync Status panel.
  Before any output mutation, playback also requires a complete light-state snapshot; a bridge
  that cannot return every selected light is reported as a startup error instead of leaving a
  partial restoration plan.
  Light restoration retries each light using the active Network Retry Attempts policy. If the
  bridge remains unavailable or area deactivation fails, Live Sync Status shows a cleanup warning
  with the restored/failed/deadline count so the remaining lights can be recovered manually;
  diagnostic, preview, pause, startup-rollback, and playback cleanup all use an independent
  30-second deadline so a canceled request cannot strand the lifecycle lease indefinitely.
  If FFmpeg remains running but stops producing complete frames, the configured FFmpeg Stall
  Timeout ends synchronization through the same cleanup path; increase it for slow storage or
  hardware decoding, or lower it to recover faster from a stuck pipeline.
* **A mapping is ignored:** an enabled custom mapping must include a valid bridge address, App
  Key, Client Key, and Entertainment Area ID. To use the global target, leave all mapping target
  fields blank; the mapping list will show **Inherited from global**. A mapping with **Enable Hue
  Sync for this user** unchecked intentionally leaves that user's playback unchanged.
* **A saved target needs attention:** open **System Diagnostics** and click **Validate Saved
  Targets**. The report checks each enabled default or per-user target separately, including
  inherited mappings, without starting a DTLS stream or changing light state. Missing keys,
  unreachable bridges, deleted areas, and empty entertainment areas are reported directly.
