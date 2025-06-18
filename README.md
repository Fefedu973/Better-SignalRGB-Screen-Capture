# Better-SignalRGB-Screen-Capture
A software that allows you to get much more customizable and pleasent screen ambiance effect on signalRGB

## Requirements Specification

*Screen-capture service for enhanced **SignalRGB** ambilight effects (Windows-only, open-source)*

---

### 1  Purpose & Context

Provide a lightweight, headless screen-capture service that overcomes SignalRGB’s current limits (no HDR, fixed single-monitor capture, no custom regions) while remaining easy to embed in its “Effect” tab through a simple local API.

---

### 2  Target Environment

* **Operating system:** Windows 10/11 (x64)
* **Form factor:** Background service with optional system-tray UI
* **Display scenarios:** Any mix of SDR and HDR monitors; up to all connected screens
* **Typical output resolution:** 800 × 600 (sufficient for ambilight sampling)

---

### 3  Capture Pipeline

| Aspect              | Requirement                                                                                                                                   |
| ------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| **Back-end**        | Use the most efficient capture path available on Windows: DXGI Desktop Duplication API preferred; fall back to GDI/OpenCV where necessary.    |
| **FPS**             | User-configurable 1 – 60 fps (default: 24 fps).                                                                                               |
| **HDR handling**    | Detect HDR surfaces; apply fast, high-quality tone mapping to SDR (BT.709) while preserving vivid colour and avoiding clipping or banding.    |
| **Multi-surface**   | Simultaneously capture any subset of: full displays, chosen process windows, or arbitrary rectangular regions—even across monitor boundaries. |
| **Transformations** | Real-time move / scale / rotate individual capture layers on a virtual canvas before output.                                                  |

---

### 4  Operating Modes

| Mode             | Trigger & Scope                                                                                       | Typical Use                              |
| ---------------- | ----------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **Screen Mode**  | Autostarts; records one or many full monitors.                                                        | Whole-desktop ambience.                  |
| **Process Mode** | Watches for a target executable; begins capture of that window only when the process is present.      | Game-only ambience.                      |
| **Region Mode**  | Uses a hot-key driven “rubber-band” selector to define one or more rectangles; autostarts thereafter. | Precise focus area (e.g., media player). |

---

### 5  User Interface & UX

* **Framework:** PyQt-Fluent-Widgets for native, Fluent-style look.
* **System tray:**

  * Left-click → quick menu (Start/Stop, Current mode, Exit).
  * Right-click → *Settings* window.
* **Settings window:**

  * Mode selection & per-mode capture list.
  * Resolution & FPS slider.
  * Tone-mapping quality toggle.
  * Theme picker (light / dark + accent colours).
  * Startup options (launch with Windows, auto-record on launch).
  * Hot-key assignment & disable checkbox (reserve **Win + Alt + C** by default).
* **Live preview:** Optional 1 fps thumbnail per capture source for verification.

---

### 6  Local Streaming & API

| Component               | Details                                                                                                                                                    |
| ----------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Embedded web server** | Spins up automatically on `127.0.0.1:<port>` (dynamic conflict-free port). Provides MJPEG or low-latency WebSocket stream of the composite 800 × 600 feed. |
| **REST/JSON API**       | No authentication (localhost-only). Exposes GET/POST endpoints to: list sources, switch mode, modify per-source transform, change FPS, toggle start/stop.  |

---

### 7  Performance Targets

* End-to-end latency ≤ 35 ms at 24 fps, 800 × 600.
* CPU ≤ 10 % on a mid-range quad-core (e.g., Ryzen 5 5600G) when capturing one 1080p HDR monitor.
* GPU leverage where possible for capture and tone-mapping; fall back gracefully if unavailable.

---

### 8  Installation & Updates

* **Build:** Compiled with **Nuitka** → single-file executable.
* **Installer:** Inno Setup wizard (silent mode switch for power users).
* **Auto-update:** Check GitHub releases JSON on launch; prompt in tray menu, download differential patch, restart seamlessly.

---

### 9  Openness & Licensing

* **Repository:** Public GitHub under MIT Licence.
* **Documentation:**

  * Markdown README with quick-start.
  * Full API reference (OpenAPI YAML).
  * Contribution guide & code-style rules (black + isort).

---

### 10  Non-Functional Requirements

* Written in type-annotated Python 3.12+.
* Modular architecture (capture, composite, stream, UI, update, api, core utils).
* Unit-test coverage ≥ 80 % for core modules; GitHub Actions CI.
* Graceful failure handling (e.g., HDR not supported → fall back to SDR pass-through).
* All third-party libs dependency-pinned; reproducible builds.

---

### 11  Future-Proofing (Stretch Goals)

* H.264 low-bit-rate RTP output option.
* Optional NT Service mode (no UI).
* Plug-in system for custom colour-sampling algorithms.

---

### 12  Glossary

* **Ambilight** – peripheral RGB lighting mimicking on-screen colours.
* **Tone mapping** – algorithm translating HDR luminance range into displayable SDR.
* **DXGI Desktop Duplication** – Windows API for high-speed desktop capture.

---

> **Summary:** The project delivers a Windows-only, open-source, tone-mapped screen-capture service with three automatic capture modes, a Fluent tray UI, a minimal local REST/MJPEG interface, and a hassle-free installer/updater—tailored to feed live 800×600 video into SignalRGB’s effect engine without taxing system resources.
