# SIRK Remote Desktop — low latency architecture

## Measured baseline

The 1.0.15 implementation uses the realtime binary dirty-region transport. The legacy
compatibility fallback performs a complete `desktop.snapshot` command cycle for every frame:

```text
browser POST -> Portal broker -> Agent long-poll -> session pipe ->
GDI StretchBlt -> JPEG -> Agent check-in -> Portal result wait -> browser decode
```

Frames are requested sequentially. This prevents a stale-frame queue, but also
limits throughput to one frame per complete control-plane round trip. Pointer
movement was additionally limited to one update per 100 ms.

## Current measured state (1.0.15)

The production data plane bypasses the command/result broker. It uses DXGI
Desktop Duplication, dirty/move metadata, scaled JPEG atlases, sharp idle
refinement, a safe one-frame channel and a direct signed binary upload to
Portal. A local recursive Portal benchmark at 3440x1440 on a physical 59 Hz
display reached 56.4 changed frames/s at 0.72 Mbit/s with p50/p95 frame age
5/17 ms. A static surface produces no image payload; a visibly blinking
console cursor produced only an 11x15 delta at approximately 0.11 Mbit/s.

Hardware probing on the same Intel UHD host found the Intel Quick Sync H.264
MFT. Independent D3D11 zero-copy tests using the Windows Media Foundation
`display_remoting` scenario measured approximately 51 FPS at native
3440x1440. A synthetic 1920x1080/60 Quick Sync test encoded at approximately
178 FPS. These measurements prove hardware capacity, but the Intel driver did
not hold a 1 Mbit/s ceiling for high-motion content. The production path must
therefore combine GPU scale/colour conversion, hardware H.264 and congestion
feedback; merely replacing JPEG with full-screen H.264 is insufficient.

The direct implementation now initializes and drives the asynchronous Intel
MFT without FFmpeg. A D3D11 video processor scales BGRA 3440x1440 to NV12
1920x800 in 2.84 ms p50/5.42 ms p95. The Intel encoder processes synthetic
NV12 1280x720/60 at 204 FPS with encode p50 3.64 ms/p95 10.96 ms and produces
0.893 Mbit/s when configured for 500 kbit/s. At 1920x1080 the tested driver
enforces an effective floor near 2.1 Mbit/s, so the adaptive profile must use
1280x720 for the strict sub-1-Mbit/s mode.

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
| Auto | 8–60 | dynamic, at least 1600 | dynamic | continuous link adaptation |
| Smooth | 60 | native/1920 | 70–80 | normal GUI work |
| Sharp text | 30 | native/1920 | 80–90, 4:4:4/lossless regions | administration and text |
| Weak link | 20 | 1600 | 60–70 | constrained WAN |
| Minimum transfer | 8 | 1920 | 65–70 | readable emergency access below 1 Mbit/s |

Auto uses rolling RTT, frame p50/p95, bitrate, dropped frames, decode/render
time and congestion feedback. Text readability takes priority over animation:
the constrained profiles reduce encoded FPS and bitrate before aggressively
reducing spatial resolution, then send a sharp refresh when the screen becomes static.

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
