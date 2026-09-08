# Hue stream protocol reference

[Configuration](CONFIGURATION.md) | [Release readiness](../RELEASE_READINESS.md)

## Evidence Boundary

The v2 serializer is checked against two independently maintained implementations:

- [Q42 HueApi.Entertainment 3.3.0](https://github.com/Q42/Q42.HueApi/blob/f8667625902a40dd544dd152d3b5ca6da29bfa50/src/HueApi.Entertainment/Models/StreamingGroup.cs#L77), commit `f8667625902a40dd544dd152d3b5ca6da29bfa50`.
- [Hyperion's v2 sender and example](https://github.com/hyperion-project/hyperion.ng/blob/6065260b0b9e29221bddf9e37465bee0df30874d/libsrc/leddevice/dev_net/LedDevicePhilipsHue.cpp#L168), commit `6065260b0b9e29221bddf9e37465bee0df30874d`.

Philips' entertainment documentation redirects to login. These sources and test
fixtures are corroborating implementation evidence, not a first-party specification
or captured physical-bridge acceptance. Hardware acceptance remains a release gate.

## Byte Layout

Offsets are zero-based. The plugin emits RGB mode only.

| Offset | Bytes | Content |
| --- | ---: | --- |
| 0 | 9 | ASCII `HueStream` |
| 9 | 2 | Version `02 00` |
| 11 | 1 | Existing wrapping sequence counter |
| 12 | 2 | Zero reserved bytes |
| 14 | 1 | RGB color space `00` |
| 15 | 1 | Zero; both inspected implementations emit zero |
| 16 | 36 | Lowercase hyphenated entertainment-configuration UUID in ASCII; no NUL |
| 52 + 7*k | 1 | Configuration channel ID, unsigned byte |
| 53 + 7*k | 2 | Red, unsigned big-endian RGB16 |
| 55 + 7*k | 2 | Green, unsigned big-endian RGB16 |
| 57 + 7*k | 2 | Blue, unsigned big-endian RGB16 |

Total packet size is `52 + 7*N`. Preserve sparse `channels[].channel_id` values;
do not substitute light-resource IDs, entertainment-service IDs, or renumbered
positions. There is no v1 device-type/two-byte light-address prefix in a v2 record.
The serialization bound is the smaller of 256 addressable IDs and the DTLS payload
capacity. This does not claim that a bridge supports 256 configured channels;
only members returned by the selected configuration are eligible.

The maintained senders use a fixed sequence value; receiver ordering requirements
were not established. The plugin preserves its existing byte counter and rollover.
Q42 and Hyperion disagree in comments about byte 15; no nonzero mode is enabled.

## Area Ownership

`SendColors` requires a hyphenated UUID and binds one area to each DTLS lifecycle.
A configuration-backed start binds its area before the handshake. An explicit
transport start binds on its first valid send. A different area is rejected before
reconnect or output; internal reconnects and keepalives retain the binding. Public
stop/replacement clears it.

Configuration-backed starts reject missing/malformed UUIDs before transport work.
Frames are copied into bounded private storage before asynchronous reconnects or
transport callbacks, so caller buffer reuse cannot change a pending frame or its
keepalive cache. The synchronous serializer validates the entries actually written.

`BuildHueStreamPacket(areaId, colors)` builds a standalone packet without changing
the active area. The retained `BuildHueStreamPacket(colors)` entrypoint requires an
already bound area and otherwise throws, rather than emitting an incomplete packet.
The serializer uses one exact-sized byte array and preserves the six supplied color
bytes. Packet binding does not resolve the separate paused-playback retarget policy.

## Color Conversion

Production video, audio, cinema, and preview paths apply their explicit brightness,
gain, tint, and effect policies in RGB8, then replicate each byte into both RGB16
bytes: `v * 257`. Black maps to `0000`; white maps to `ffff`; `1` remains `0101`.
Effects and fades continue using this byte-replicated representation. This is not
a new arbitrary-precision RGB16 interpolation feature.

The old compatibility conversion used `floor(v / 2) * 257`, limiting full-scale
output to `7f7f` and discarding half the component levels. Corrected numeric output
is higher at unchanged settings; perceived brightness is hardware-dependent and
not necessarily linear. No saved brightness settings are rewritten. Operators who
want lower intensity can adjust Output Brightness or the relevant scene/cinema
brightness setting. The deliberately low-intensity diagnostic probe remains low.

## Offline Checks

- Compare the full 80-byte Hyperion source-comment example, including its UUID,
  four channel IDs, RGB16 bytes, and sequence value.
- Check an independently derived channel-255/asymmetric-RGB16 packet for endian
  mistakes, UUID encoding, and the 59-byte length.
- Reject invalid UUIDs and channel 256 before transport/reconnect work; exercise
  same-lifecycle area changes, stop/replacement, keepalive, and rollover behavior.
- Compare actual production color conversion, explicit dimming, effects/fades,
  cinema tint, and fallback white. Prototype benchmarks check byte equivalence
  with the production serializer and do not claim full-frame or hardware FPS.
