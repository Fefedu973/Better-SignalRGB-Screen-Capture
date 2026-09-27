# Native OpenRGB integration — API v1

This is the **Better-side** integration for the OpenRGB Room fork. Better owns capture,
sources, editing, saved scenes and composition. OpenRGB consumes an image texture and
implements its own shader/effect processing. No additional bridge process or consumer
WebView is required. Optional Website capture in Better still uses WebView2.

The receiving Effects implementation and physical lighting have **not** been connected
by this change. Publisher/Reader interoperability is tested with the actual C++ Reader,
not a substitute transport. The old [upstream assessment](openrgb-integration-assessment.md)
is historical and does not describe the current Room architecture.

## Enable and discover

In Better, enable **Settings → OpenRGB → Native OpenRGB output**. This preference defaults
to off and is independent of the SignalRGB `Enabled` and web `WebEnabled` switches.
The output follows the normal canvas resolution: 320×200, or 800×600 with HQ. Geometry
always remains 320×200. Capture play/pause, saved scenes and source editing retain their
existing UI/lifecycle. Pausing/stopping capture does not stop the native control API.

A native client reads this well-known file as the same Windows user:

```
%LOCALAPPDATA%\Better_SignalRGB_Screen_Capture\ApplicationData\NativeOutput\connection.json
```

An explicitly configured `LocalSettingsOptions.ApplicationDataFolder` changes that base
directory too; test profiles never use the personal descriptor. Example (values fictional):

```json
{
  "apiVersion": 1,
  "baseUrl": "http://127.0.0.1:49152",
  "authorizationScheme": "Bearer",
  "token": "<64-hex-character-secret>",
  "instanceId": "9df6b94e-fd7a-4581-a08b-292636578279",
  "processId": 1234,
  "discoveryPath": "/api/native/v1/discovery"
}
```

The port is allocated by Windows, separate from existing HTTP/HTTPS streaming ports.
The directory/file have a protected current-user DACL. The token is random per activation,
is not put into URLs, logs or browser settings, and is removed on clean shutdown/disable.
An exclusive descriptor lock prevents two producers claiming the same profile. After a
crash the file may remain, but its server is gone; retry reading it when Better restarts.

**Every** request, including discovery/status, requires `Authorization: Bearer <token>`.
Only loopback requests with the descriptor's exact Host are accepted. Any `Origin` or
`Sec-Fetch-Site` header is rejected; this is a native API, deliberately without the web
server's permissive CORS. Never send credentials to an arbitrary URL obtained elsewhere,
forward them to a remote service, or embed them in an effect webpage. Same-user processes
are the trust boundary; this is not a sandbox against malicious code running as that user.

## Endpoints

All paths below are relative to `baseUrl`, under `/api/native/v1`. JSON uses camelCase.
Request bodies use `Content-Type: application/json`, maximum 16 KiB and depth 12. Unknown
body fields are rejected. There are at most 16 concurrent requests and one mutation in
flight; clients retry `409 command_in_progress` instead of accumulating command queues.

| Method/path | Purpose |
| --- | --- |
| GET `/discovery` | API/transport versions, capabilities, stable output IDs/channels, dimensions and limits |
| GET `/status` | Capture/control state, lease owner label, effective settings, publisher status/metrics and current frame binding |
| GET `/scenes` | Saved scenes `{id,name}`; ID is a stable UUID |
| GET `/active-scene` | Requested/UI scene state plus last frame-effective state |
| GET `/states/{rawGeneration}` | Immutable rendering/scene/coverage metadata for that raw generation |
| POST `/leases` | Acquire the single global control lease, optionally select a scene and appearance overrides |
| PUT `/leases/{leaseId}/renew` | Renew the owning client's lease |
| PUT `/leases/{leaseId}/scene` | Select a saved scene through Better's normal capture lifecycle |
| PUT `/leases/{leaseId}/appearance` | Replace metadata-only temporary appearance overrides; null clears them |
| DELETE `/leases/{leaseId}` | Release, remove overrides and conditionally restore the previous canvas |

Discovery advertises ORGBFRM1 v1, BGRA8 opaque sRGB, a 128-byte header, and stable outputs
`canvas-raw` and `canvas-coverage`. Channels are `BetterCapture-<profile/user hash>-Raw`
and `...-Coverage`, not thousands of virtual LEDs. The profile hash is deterministic;
discover it rather than hardcoding it. The output becomes usable after the first committed
frame, independently of whether anyone has opened `/stream`, `/web-stream` or the webpage.

Typical status includes:

```json
{
  "apiVersion": 1,
  "control": {
    "revision": 12,
    "scene": { "activeSceneId": "00000000-0000-0000-0000-000000000001",
      "activeSceneName": "Desk", "manualRevision": 4, "stateRevision": 8,
      "sceneLoading": false, "isRecording": true, "isPaused": false,
      "isStopping": false, "error": null },
    "lease": { "clientId": "openrgb-room/ambient", "ttlSeconds": 30,
      "expiresAt": "2026-09-27T15:00:00Z", "expired": false, "appearanceRevoked": false }
  },
  "output": { "enabled": true, "status": "ready", "error": null,
    "publishedFrames": 100, "droppedFrames": 2, "effectiveState": {} }
}
```

Examples abbreviate appearance/rendering fields. `activeSceneId:null` means the canvas is
custom/edited rather than a known loaded preset; selected library rows are not mistaken
for the active scene. A legacy startup layout restored from `SavedSources` also has no
inferred preset identity until a scene is explicitly loaded or saved. Changes in geometry, quality, source presence, scene/capture state
or effective appearance produce a new effective state. Scene loading is observable.

## Exact frame association and reading

`FrameSurface/FrameSurface.h` in the Room repository is authoritative for binary layout,
mutex names, capacities and lifetime. Verification provenance and commands are in
[the publisher tests](../tests/BetterSignalRGB.NativeOutputTests/README.md).
No reserved field is changed, and the existing Reader does not require modification.

Both channels allocate 1,920,000 payload bytes from the start so retained reader mappings
can accommodate HQ. Width, height and stride describe the current image, not capacity.
Rows are top-down, stride is width×4, pixels are BGRA with alpha 255, in sRGB. The raw image
is composed over black **before** global picture placement, color filters or any halo.
Coverage stores the accumulated opacity as equal B/G/R bytes, also with alpha 255.
The Contours silhouette is a separate opaque union of contributing geometry polygons.

The published envelope is:

```json
{
  "stateRevision": 9,
  "controlRevision": 12,
  "scene": { "stateRevision": 8, "activeSceneId": "00000000-0000-0000-0000-000000000001" },
  "rendering": { "version": 1, "schema": "better.native-rendering", "stateRevision": 9 },
  "image": { "outputId": "canvas-raw", "channel": "BetterCapture-example-Raw",
    "generation": "12345678901234567890", "sequence": "1", "width": 800,
    "height": 600, "stride": 3200, "format": "BGRA8_OPAQUE_SRGB" },
  "coverage": { "outputId": "canvas-coverage", "channel": "BetterCapture-example-Coverage",
    "generation": "12345678901234567891", "sequence": "1", "width": 800,
    "height": 600, "stride": 3200, "format": "BGRA8_OPAQUE_SRGB" }
}
```

Generation/sequence are decimal **strings** in JSON to preserve uint64 precision. They
are not state revision counters. New scene/geometry/settings states recreate publishers
with new generations on the same channels; normal image updates advance raw sequence.
Coverage is committed first and stays immutable for that state. An envelope is announced
only after both channel publications succeed. Failed/busy attempts do not acknowledge it.

Receiver algorithm:

1. Read raw using `room_surface::Reader::ReadLatest` and retain its own BGRA vector,
   generation, sequence, width/height/stride. Use a bounded latest-image cache.
2. Fetch/cache `/states/{rawGeneration}`. On 404, retry the latest raw image: the generation
   may still be committing or may have fallen out of the bounded eight-state history.
3. Require raw sequence **at least** the envelope's first sequence, exact raw generation
   and dimensions. `/status` reports a current sequence; `/states` intentionally retains
   the first sequence. Do not demand equality with the latter for subsequent images.
4. Read coverage and require its exact generation **and** sequence, dimensions, stride
   and format from that envelope.
   Channels are independently synchronized; if coverage already belongs to a newer
   generation, discard the incomplete pair and restart at step 1.
5. Only now upload the image/coverage and apply the matching rendering metadata. Never
   pair an older image with the current `/status` geometry by assumption.

The mutex protects the whole header+payload; sequence polling alone is insufficient.
Multiple readers independently see the latest image, without consuming it for others.
A busy mutex drops publication rather than blocking capture/UI. The in-process queue
contains one owned pending frame; the consumer copies synchronously and never retains
a returned pool buffer. Shutdown retires the lifetime marker even under contention.

The healthy publication worker refreshes header timestamp about every 250 ms, including
unchanged/paused pictures, without incrementing sequence or copying pixels. It first
checks the application context; a hung context stops refresh. With a recommended 2,000 ms
TTL, `Stale` means an unresponsive publisher; `Unavailable` includes shutdown/crash.
The existing C++ Reader returns `Unchanged` for a live static image but does not update
the cached `Frame.timestamp_ms` on that result. Treat successful `Unchanged` as live;
do not expire it again using that older cached timestamp. Timestamp is uptime freshness,
not the age of captured screen content. On `Unavailable`/`Stale`/`Invalid`, show a source
error and reconnect with bounded backoff; never treat a dead producer's last image as live.
`Busy` means transient mutex contention: retry with a bounded wait/backoff and do not
treat the cached image as a newly received frame.

This path copies CPU pixels into shared memory and subsequently into OpenGL. It is
**not zero-copy**, and it is not a shared GPU texture implementation.

## Client lifecycle and temporary control

For a raw texture, simply discover/read the output: **do not disable Better's glow**.
Raw pixels never contain that glow. Acquire control only if an effect actually needs to
select a scene or override metadata. There is one application-wide scene, not independent
scenes for concurrent effects. Share one lease in the OpenRGB source provider when needed.

Acquire example:

```http
POST /api/native/v1/leases
Authorization: Bearer <token>
Content-Type: application/json

{"clientId":"openrgb-room/ambient","ttlSeconds":30,
 "sceneId":"00000000-0000-0000-0000-000000000001",
 "overrides":{"ambilightStyle":"Contours","screenWidth":260,"screenHeight":160,"screenX":30,"screenY":20}}
```

`control.lease.id` in the response is the owner's opaque capability. Store it in memory;
it is deliberately absent from public status/discovery. A second acquisition returns
`409 lease_conflict`, even for the same client label. Renew every ~10 seconds with
`PUT /leases/{id}/renew` and `{"ttlSeconds":30}`. Lease bounds are 1..300 seconds; expiry
uses monotonic time, so changing the wall clock does not extend ownership. UTC expiry
in JSON is informational. Release on effect stop; expiry covers disappearance/crash.

Scene changes use `PUT /leases/{id}/scene` and `{"sceneId":"..."}`. Selection is dispatched
to the UI and reuses the existing restore/undo capture lifecycle, retaining paused/running
intent. A concurrent explicit stop wins over a scene's attempted restart. Busy editing,
stopping or another scene operation returns `409 scene_busy` for later retry.

Responses after selection/override/release are `200` with `result:"effective"` only when
a matching frame has been published. They include `targetControlRevision`,
`targetSceneRevision`, `control`, and `effectiveState`. If no matching frame commits within
two seconds, `202 pending_frame` acknowledges only the state request. Poll status/state
until those exact revisions and scene identity are frame-effective. A manual edit or
lease transition during the initial wait returns `409 state_superseded`. After a `202`,
status polling still returns `200`: the client must detect a newer `control.revision`
than its target and abandon that superseded request, rather than wait for it forever.
Never report a superseded request effective.
A scene selected while paused may contain sources without frames until capture resumes;
geometry/availability are explicit and no old source image is relabeled as a new scene.

Overrides are a **native metadata layer**, not changes to web/SignalRGB preferences or
raw pixels. `PUT /leases/{id}/appearance` replaces the patch using `{"overrides":{...}}`;
`{"overrides":null}` clears it. Supported fields/defaults/ranges are documented in
[rendering v1](native-rendering-v1.md). `enabled` and `webEnabled` are not overridable.
The receiving renderer chooses either raw pixels or the appearance recipe and applies
filters once. Better's persistent appearance update service is never called by leases.

Manual-user precedence:

- A manual appearance edit removes temporary overrides immediately and revokes further
  appearance overrides for that lease (`manual_override`). The new saved preference wins.
- A manual canvas edit promotes the current layout to the user's durable scene, revokes
  automatic scene restoration, and rejects further scene selection from that lease.
- Without manual edits, release/expiry restores the previous source snapshot, active
  identity and undo history. A temporary scene is never written as `SavedSources`.
- If restoration meets an active edit/stop, the expired lease remains pending, its
  appearance overrides are removed, and a bounded timer retries when the canvas is free.
  Another client cannot take ownership through this pending restoration.
- Disabling native output immediately retires the transport/API and expires control;
  deferred scene restoration can still finish through the application context.

## Diagnosis and handoff

Settings shows native output state. The authenticated `/status` distinguishes `disabled`,
`waiting_for_frame`, `waiting_for_sources`, `scene_loading`, `ready`, `paused`, `stopped`,
`application_unresponsive`, `transport_degraded`, and `output_error`. Errors include channel ownership/permissions,
invalid geometry/capacity and unavailable application context. Fix the cause, retry the
operation or toggle native output to restart; never delete another live producer's mapping.
`droppedFrames` includes coalesced and contended attempts. `compositionMilliseconds` is
the last source-decode/composition duration; `publicationMilliseconds` measures that
publication (including generation setup when needed); `frameAgeMilliseconds` is elapsed
composition-to-publication time measured **at publication**, not end-to-end capture latency.
Short mutex contention is tolerated. Publication or heartbeat failures lasting at least
two seconds report `transport_degraded` with the failed operation and reason. Raw and
coverage heartbeats are checked separately; a live old image cannot conceal a repeatedly
rejected new publication. Recovery clears each failure only after its own operation succeeds.

OpenRGB agent handoff: implement one reusable Better image source using the descriptor,
authenticated discovery/status API and existing Reader; obey the generation-bound pair
algorithm above; upload BGRA/coverage to ordinary textures; share leases across effects;
surface conflict/busy/expiry states; validate shaders with the synthetic fixtures in
`tests/fixtures/native-rendering-v1`. Keep native GLSL processing on the OpenRGB side.
Do not add screen/source/canvas editors there or model image pixels as virtual LEDs.

The first integration intentionally retains the compositor's existing source-JPEG decode
cache. It removes the **composite** JPEG encode and receiving JPEG decode from native
delivery, not all image compression from capture. GDI composition is CPU based. Raw
capture buffer sharing and shared GPU textures are later optimizations requiring measured
benefit and explicit ownership/device-loss handling. GDI/web rasterization and 8-bit
flattening differ at antialiased/low-opacity edges; the rendering guide describes the
precision limits and provides web reference outputs instead of promising pixel identity.

See the [validation and performance report](native-openrgb-validation.md) for local test
results, the isolated WinUI smoke test, reproducible commands and measured remaining costs.
