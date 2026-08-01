# SIRK Remote Desktop — low latency architecture

## Measured baseline

The 1.0.1 implementation is a compatibility transport, not the final realtime
transport. Every frame performs a complete `desktop.snapshot` command cycle:

```text
browser POST -> Portal broker -> Agent long-poll -> session pipe ->
GDI StretchBlt -> JPEG -> Agent check-in -> Portal result wait -> browser decode
```

Frames are requested sequentially. This prevents a stale-frame queue, but also
limits throughput to one frame per complete control-plane round trip. Pointer
movement was additionally limited to one update per 100 ms.

## Current measured state (1.0.4 development)

The compatibility data plane now bypasses the command/result broker. It uses
DXGI Desktop Duplication, dirty-region atlases, a one-frame channel and a
direct signed binary upload to Portal. A local moving-window benchmark at
3440x1440 measured 24.25 FPS, frame age p50 13 ms/p95 25 ms and capture p50
6.35 ms. JPEG still consumed 5.864 Mbit/s, so this is not the final codec.

Hardware probing on the same Intel UHD host found the Intel Quick Sync H.264
MFT. Independent D3D11 zero-copy tests using the Windows Media Foundation
`display_remoting` scenario measured approximately 51 FPS at native
3440x1440. A synthetic 1920x1080/60 Quick Sync test encoded at approximately
178 FPS. These measurements prove hardware capacity, but the Intel driver did
not hold a 1 Mbit/s ceiling for high-motion content. The production path must
therefore combine GPU scale/colour conversion, hardware H.264 and congestion
feedback; merely replacing JPEG with full-screen H.264 is insufficient.

Reproduce the hardware probe with `tools/Test-HardwareDesktopPipeline.ps1`.
FFmpeg is a test oracle only and is not shipped as an Agent runtime dependency.

MeshCentral is faster because it uses a persistent binary relay, tile updates,
local cursor commands and optional WebRTC. It is a useful baseline, but not the
target architecture.

## Target data plane

The secure policy/check-in channel remains the control plane. An authorized
desktop session creates a separate short-lived data plane:

- DXGI Desktop Duplication first, Windows Graphics Capture second and GDI only
  as a compatibility fallback;
- DXGI dirty rectangles, move rectangles and pointer metadata;
- a one-frame capture/encode queue with latest-frame-wins semantics;
- local cursor rendering; cursor position and shape never wait for a screen
  frame;
- hardware H.264 low-delay encoding for motion and lossless/RGB tile updates
  for sharp text;
- reliable, highest-priority input/control flow;
- unreliable or time-limited frame flow over WebRTC or QUIC datagrams;
- WebSocket/TCP fallback with compression disabled, `TCP_NODELAY`, logical
  channel priority and stale-frame dropping;
- a sharp refresh frame after motion stops.

No protected Device ID, policy state, evidence chain, quarantine state or
Portal credential is part of the ephemeral desktop data plane.

## Profiles

| Profile | Active FPS | Resolution | Quality | Intended use |
| --- | ---: | ---: | ---: | --- |
| Auto | 15–40 | dynamic | dynamic | continuous link adaptation |
| Smooth | 30, burst 40 | native/1920 | 70–80 | normal GUI work |
| Sharp text | 20–30 | native/1920 | 80–90, 4:4:4/lossless regions | administration and text |
| Weak link | 15 | 1280 or 0.7 scale | 50–65 | constrained WAN |
| Minimum transfer | 5–10 | 960 or lower | 30–45 | emergency access |

Auto uses rolling RTT, frame p50/p95, bitrate, dropped frames, decode/render
time and congestion feedback. It lowers scale before sacrificing interactive
continuity and sends a sharp refresh when the screen becomes static.

## Acceptance gates

- compatibility fallback: at least 1920x1080 at 25 FPS while dragging a window
  on the local LAN;
- production smooth profile: 1920x1080 at 60 FPS on the local LAN for normal
  administrative desktop workloads;
- production constrained profile: rolling transfer at or below 1 Mbit/s while
  preserving interactive continuity; unrestricted full-screen video is not
  evaluated as an administrative desktop workload;
- frame latency p50 below 40 ms and p95 below 80 ms;
- mouse-to-photon and keyboard-to-screen p95 below 70 ms;
- pending frame queue never above one;
- no latency spike above 200 ms in 99% of the measured session;
- reconnect below two seconds;
- Agent RAM below 250 MB;
- visible live telemetry for FPS, p50/p95, input dispatch/ack, capture, encode,
  transport, decode, render, bitrate, dropped frames, queue depth, backend,
  codec, profile and detected link state.

Sub-10 ms can be a transport/input-dispatch measurement on a LAN. It is not a
credible capture-to-photon target for a 60 Hz display, whose single refresh
period is already about 16.7 ms.
