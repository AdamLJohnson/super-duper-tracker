# DisneyLand Tracker

A real-time Disneyland Resort attraction dashboard built on a fully serverless, event-driven AWS backend and a Blazor WebAssembly frontend. The system polls live wait-time and Lightning Lane data every minute, propagates changes through SNS, maintains 2-hour sparkline trends, and delivers updates to browser clients over a persistent WebSocket connection.

---

## Repository Layout

```
/
├── infra/src/Infra/          # AWS CDK stacks (C# / CDK for .NET)
├── src/
│   ├── ThemeParkPoller/      # EventBridge-triggered polling Lambda
│   ├── CurrentStateCollector/# State persistence + attraction_updated publisher
│   ├── SparklineProcessor/   # 120-min rolling sparkline maintainer
│   ├── ThemeParkApi/         # REST API Lambda (API Gateway)
│   ├── WebSocketBroadcaster/ # Real-time fan-out Lambda (WebSocket API Gateway)
│   └── DisneylandClient/     # Blazor WASM frontend
└── README.md
```

---

## Architecture Overview

```
EventBridge (1 min)
       │
       ▼
 ThemeParkPoller ──── SHA-256 diff ──── EntityStateTable (DynamoDB)
       │  (api_update)
       ▼
   UpdatesTopic (SNS)
    ├──(api_update)────────────► CurrentStateCollector
    │                                  │  writes CurrentStateTable
    │                                  │  (attraction_updated)
    │                                  ▼
    │                             UpdatesTopic (SNS)
    │                              ├──(attraction_updated)──► SparklineProcessor
    │                              │                               │ writes SparklineStore
    │                              │                               │ (sparkline_updated)
    │                              │                               ▼
    │                              │                          UpdatesTopic (SNS)
    │                              │                               │
    │                              └──(attraction_updated) ─────────┤
    │                              └──(sparkline_updated)  ─────────┤
    │                                                               ▼
    │                                                      WebSocketBroadcaster
    │                                                               │ PostToConnection
    │                                                               ▼
    │                                                       Browser Clients (WSS)
    │
    └── REST API (API Gateway) ◄──── ThemeParkApi Lambda
                                          reads CurrentStateTable
                                          reads SparklineStore
```

---

## Infrastructure (`/infra`)

All AWS resources are defined as AWS CDK stacks in C#. Run `cdk deploy --all` from `/infra` to provision every stack into the default account and region.

### PollerStack

The foundational stack. Owns the shared SNS topic and the two DynamoDB tables needed for change detection and live state storage, and provisions the two core processing Lambdas.

**Resources provisioned:**

| Resource | Purpose |
|---|---|
| `EntityStateTable` | Stores a SHA-256 hash of the last-seen API response per entity. Used exclusively by `ThemeParkPoller` for change detection. PK: `EntityId`. |
| `CurrentStateTable` | Stores the latest live state DTO for every entity. PK: `EntityId`. GSIs: `StatusIndex` (on `Status`) and `LightningLaneIndex` (on `IsLightningLane`) for efficient dashboard queries. |
| `UpdatesTopic` | The central SNS topic that carries all inter-Lambda event traffic. Exposed as a cross-stack `ITopic` reference consumed by every other stack. |
| `ThemeParkPollerFunction` | 256 MB, 30 s timeout. Triggered by EventBridge Scheduler at `rate(1 minute)`. |
| `CurrentStateCollectorFunction` | 256 MB, 30 s timeout. SNS subscription filtered to `event_type = api_update`. |

### SparklineStack

Consumes the `UpdatesTopic` from `PollerStack` and adds trend-tracking infrastructure. Exposes `SparklineTable` as a cross-stack reference for `ApiStack`.

**Resources provisioned:**

| Resource | Purpose |
|---|---|
| `SparklineStore` | Stores the sparse bucket series per attraction. PK: `EntityId`. GSI: `LightningLaneIndex` (on `IsLightningLane`) for cold-start bulk reads by the REST API. |
| `SparklineProcessorFunction` | 256 MB, 30 s timeout. SNS subscription filtered to `event_type = attraction_updated`. Publishes `event_type = sparkline_updated` back to `UpdatesTopic`. |

### ApiStack

A REST API backed by a single Lambda function. Receives cross-stack references to `CurrentStateTable` (from `PollerStack`) and `SparklineTable` (from `SparklineStack`).

**Routes:**

| Method | Path | Description |
|---|---|---|
| `GET` | `/attractions/lightning-lane` | All LL attractions via `LightningLaneIndex` GSI |
| `GET` | `/attractions/status/{status}` | Attractions filtered by status via `StatusIndex` GSI |
| `GET` | `/sparklines/lightning-lane` | 24-slot sparkline arrays for all LL attractions |

CORS is configured to `AllowOrigins: ALL_ORIGINS` with `GET` and `OPTIONS` methods.

### WebSocketStack

Provisions a WebSocket API Gateway and the two Lambda handlers that together manage the real-time push pipeline.

**Resources provisioned:**

| Resource | Purpose |
|---|---|
| `ConnectionsTable` | Tracks every active WebSocket connection ID. PK: `ConnectionId`. |
| `WsConnectionFunction` | Handles `$connect` / `$disconnect` route keys; writes or deletes the connection ID in `ConnectionsTable`. |
| `WsBroadcasterFunction` | SNS subscription filtered to `event_type IN [attraction_updated, sparkline_updated]`. Fans out to all active connections. |
| `ThemeParkWsApi` / `prod` stage | The `wss://` endpoint clients connect to. The stage's `CallbackUrl` is injected into `WsBroadcasterFunction` as `WEBSOCKET_ENDPOINT`. |

### FrontEndStack

Hosts the compiled Blazor WASM static assets.

**Resources provisioned:**

| Resource | Purpose |
|---|---|
| Private S3 bucket | Stores published `wwwroot` output. No public access; CloudFront is the sole origin. |
| CloudFront distribution | HTTPS-only, Origin Access Control (OAC). 404s are rewritten to `index.html` so Blazor's client-side router handles deep links. |

CDK outputs `FrontEndBucketName`, `FrontEndDistributionId`, and `FrontEndUrl` for use by deployment scripts.

---

## Components

### `src/ThemeParkPoller`

**Trigger:** EventBridge Scheduler — `rate(1 minute)`

Polls the public [ThemeParks.wiki API](https://api.themeparks.wiki/v1) for live entity data under the `disneylandresort` destination slug.

**Change-detection algorithm:**
1. Issue a single DynamoDB `Scan` on `EntityStateTable` to load all stored SHA-256 hashes into memory before the entity loop — one round-trip regardless of entity count.
2. For each entity in the API response, serialize it to JSON and compute `SHA256(UTF-8 bytes)`.
3. Compare against the in-memory hash. If equal, skip silently.
4. On a mismatch: `PutItem` the new hash to `EntityStateTable`, then `Publish` the raw entity JSON to `UpdatesTopic` with `event_type = api_update`.

Only genuinely changed entities generate SNS messages, keeping downstream Lambda invocations proportional to real-world change frequency rather than poll frequency.

---

### `src/CurrentStateCollector`

**Trigger:** SNS `UpdatesTopic`, filtered to `event_type = api_update`

Persists the latest live state for each entity and re-publishes a cleaned DTO for Lightning Lane attractions.

**Processing steps:**
1. Deserialize the raw `api_update` message body as `EntityLiveData`.
2. `PutItem` to `CurrentStateTable` with the `EntityId` PK, `Status` (for `StatusIndex` GSI), `IsLightningLane` (for `LightningLaneIndex` GSI), and the full original JSON as `FullData`.
3. If `IsLightningLane == true`: build an `AttractionUpdateDto` that merges paid and free Lightning Lane slots (paid takes precedence) and publish it to `UpdatesTopic` with `event_type = attraction_updated`.

Only Lightning Lane attractions emit `attraction_updated` events, so the downstream sparkline and WebSocket pipelines never process standard-queue-only rides.

---

### `src/SparklineProcessor`

**Trigger:** SNS `UpdatesTopic`, filtered to `event_type = attraction_updated`

Maintains a rolling 120-minute wait-time trend for each Lightning Lane attraction as 24 discrete 5-minute buckets.

**Bucket logic:**
- **Bucket key:** The incoming `LastUpdated` timestamp is floored to the nearest 5-minute boundary (`floor(t, 5 min)`). All updates arriving within the same window overwrite each other; the latest value wins.
- **Status handling:**
  - `Status != "OPERATING"` (DOWN, CLOSED, REFURBISHMENT, etc.) → record `0` for the bucket. Downtime is visualized as a zero-baseline rather than a data gap.
  - `Status == "OPERATING"` with a known `StandbyWaitTime` → record the wait time.
  - `Status == "OPERATING"` with `null` `StandbyWaitTime` → skip. The ride is open but not yet reporting; a gap is more honest than a false zero.
- **Pruning:** Buckets older than `now − 115 minutes` (the open left edge of the 24-slot window) are evicted on every write.
- **Storage:** The pruned sparse series is serialized as a JSON array of `{T, V}` pairs and stored in the `BucketsJson` attribute of `SparklineStore`.

**Materialization (sparse → fixed array):**

After writing, the processor projects the sparse dictionary onto a fixed 24-slot array anchored at `floor(now, 5 min)`:

```
slot[0]  = newestBucket − 115 min  ← oldest
slot[1]  = newestBucket − 110 min
...
slot[23] = newestBucket            ← newest / current
```

A **forward-fill pass** then propagates the last known value into any null slot, so a single missed poll cycle produces a flat continuation rather than a visual gap. Slots that precede the very first recorded observation remain `null` — they appear at the leading edge of a newly tracked attraction's sparkline.

The materialized 24-element array (with `null` for leading-edge slots) is published as the `event_data.Buckets` field in a `sparkline_updated` SNS message.

---

### `src/ThemeParkApi`

**Trigger:** API Gateway REST (`prod` stage) — proxied to a single Lambda function.

Routes incoming `APIGatewayProxyRequest` events by `request.Resource`:

| Route | DynamoDB operation | Notes |
|---|---|---|
| `/attractions/lightning-lane` | `Query` on `CurrentStateTable` `LightningLaneIndex` | Returns all LL attractions |
| `/attractions/status/{status}` | `Query` on `CurrentStateTable` `StatusIndex` | Filters by `Status` value |
| `/sparklines/lightning-lane` | `Query` on `SparklineStore` `LightningLaneIndex` | Materializes sparse `BucketsJson` → 24-slot array with forward-fill; returns `SparklineDto[]` |

All responses include CORS headers. The Lambda applies the same `FloorToBucket` + slot-projection + forward-fill logic as `SparklineProcessor` so the REST cold-start payload is structurally identical to the WebSocket `sparkline_updated` event payload.

---

### `src/WebSocketBroadcaster`

**Two Lambda handlers in one code asset** (single Docker build per CDK deployment):

#### `ConnectionHandler`
Invoked by API Gateway for `$connect` and `$disconnect` route keys. Writes or deletes the `ConnectionId` in `ConnectionsTable`. Never calls the Management API, so it does not need `WEBSOCKET_ENDPOINT` configured.

#### `BroadcastHandler`
**Trigger:** SNS `UpdatesTopic`, filtered to `event_type IN [attraction_updated, sparkline_updated]`

1. Pages through `ConnectionsTable` with a paginated `Scan` to collect all active connection IDs.
2. For each SNS record: reads the `event_type` message attribute and calls `BuildWsPayload`, which wraps the SNS message body in the WebSocket envelope without a full DTO round-trip:
   ```json
   { "event_type": "attraction_updated", "event_data": { "EntityId": "...", ... } }
   ```
3. Posts the envelope to every connection ID via the API Gateway Management API (`PostToConnection`).
4. Connections that return HTTP 410 Gone (client disconnected without triggering `$disconnect`) are collected and pruned from `ConnectionsTable` after the broadcast loop.

`BuildWsPayload` reads `event_type` from the SNS message attribute verbatim, so any future event type published to `UpdatesTopic` is broadcast to clients without requiring changes to this Lambda — only the SNS filter allowlist in `WebSocketStack` needs updating.

---

### `src/DisneylandClient`

A Blazor WebAssembly SPA served from CloudFront. All API communication happens client-side; there is no server-side rendering.

#### Service layer

**`WebSocketService`** (`Services/WebSocketService.cs`) — a singleton that maintains a resilient WebSocket connection:
- Exponential backoff on failure: 2 s → 4 s → 8 s → 16 s → 30 s (capped).
- Dispatches inbound messages by `event_type` to registered `Func<JsonElement, Task>` handlers (case-insensitive). New event types require only a new `WsService.On(...)` call — no changes to the service itself.
- Exposes a `StateChanged` event and `State` enum (`Disconnected / Connecting / Connected / Reconnecting`) consumed by the UI to render the connection status pill.

**`IThemeParksApi`** / Refit client — typed HTTP client for all three REST endpoints, configured with case-insensitive JSON deserialization and string-enum support.

#### `Pages/Home.razor` — synchronization strategy

Three independent mechanisms keep the dashboard current:

| Mechanism | Trigger | Purpose |
|---|---|---|
| **Initial load** | `OnInitializedAsync` | REST fetch of all LL attractions + all LL sparklines on cold start |
| **WebSocket push** | `attraction_updated` / `sparkline_updated` events | Sub-second targeted card updates as changes arrive |
| **Reconnect catchup** | `Reconnecting → Connected` state transition | Full REST re-fetch after a WebSocket drop to reconcile any missed events |
| **Heartbeat timer** | `System.Threading.Timer`, 5-minute period | Safety-net REST refresh even when the WebSocket remains `Connected` but silently misses messages |

The `_initialRefreshComplete` flag prevents `OnWsStateChanged` from triggering a catchup fetch during the initial `Connecting → Connected` handshake, which races with the initial REST load. The `_lastKnownState` field ensures only `Reconnecting → Connected` transitions (genuine recoveries) trigger a catchup — not cold-start `Connecting → Connected` ones.

The heartbeat timer is initialized **after** `_initialRefreshComplete = true`, so its first tick fires exactly 5 minutes after the initial load and never races with it. `RefreshDataAsync` guards against concurrent execution with an `if (isLoading) return` early exit.

#### `Components/AttractionCard.razor`

A self-contained component with three parameters:

| Parameter | Type | Description |
|---|---|---|
| `Attraction` | `AttractionDto` | Live attraction data |
| `Sparkline` | `int?[]` | 24-slot trend array from `_sparklines` dictionary |
| `PreviousWaitTime` | `int?` | Previous standby wait for trend-arrow calculation |

**SVG sparkline** — rendered as a `<polyline>` (stroke) overlaid on a `<polygon>` (fill area) inside a `viewBox="0 0 240 40"` SVG with `preserveAspectRatio="none"`:
- `null` slots are treated as `0` for Y-axis calculations so the line is always continuous across gaps.
- Y-coordinates are normalized to the maximum value in the array with 4-unit vertical padding.
- If all values are `0`, a flat baseline is rendered at `y = 36`.
- Point coordinates are formatted with `FormattableString.Invariant` to avoid decimal-comma locale issues in non-en browsers.
- The entire sparkline container is omitted (`@if`) when `Sparkline` is null or empty, keeping newly added attraction cards uncluttered.

---

## Data Flow

Tracing a single attraction update from poll to browser:

```
1. EventBridge fires ThemeParkPoller (rate: 1 min)

2. ThemeParkPoller
   ├── GET https://api.themeparks.wiki/v1/entity/disneylandresort/live
   ├── Scan EntityStateTable  (all SHA-256 hashes, one round-trip)
   └── For each changed entity:
       ├── PutItem EntityStateTable  (new hash)
       └── Publish UpdatesTopic     event_type=api_update

3. SNS delivers to CurrentStateCollector  (filter: api_update)
   ├── PutItem CurrentStateTable  (live state + GSI attributes)
   └── If IsLightningLane:
       └── Publish UpdatesTopic   event_type=attraction_updated

4a. SNS delivers to SparklineProcessor  (filter: attraction_updated)
    ├── GetItem SparklineStore     (existing sparse buckets)
    ├── Upsert current bucket      (0 if not OPERATING, wait time if OPERATING)
    ├── Prune buckets > 120 min old
    ├── PutItem SparklineStore     (updated sparse series)
    ├── Materialize 24-slot array  (with forward-fill)
    └── Publish UpdatesTopic       event_type=sparkline_updated

4b. SNS delivers to WsBroadcasterFunction
    (filter: attraction_updated OR sparkline_updated)
    ├── Scan ConnectionsTable      (all active connection IDs)
    └── For each connection:
        └── PostToConnection  { event_type, event_data: { ... } }

5. Browser (DisneylandClient)
   ├── WebSocketService.DispatchMessage parses envelope
   ├── attraction_updated → HandleAttractionUpdateAsync
   │   └── Patches _attractions list, re-sorts, StateHasChanged
   └── sparkline_updated → HandleSparklineUpdateAsync
       └── _sparklines[entityId] = newBuckets, StateHasChanged
           └── AttractionCard re-renders with updated SVG polyline
```

---

## Local Configuration

The Blazor client reads infrastructure endpoint URLs from a file that is **not committed to the repository**:

**`src/DisneylandClient/wwwroot/appsettings.json`**

```json
{
  "ApiConfig": {
    "RestBaseUrl": "https://{api-id}.execute-api.{region}.amazonaws.com/prod",
    "WebSocketUrl": "wss://{ws-api-id}.execute-api.{region}.amazonaws.com/prod"
  }
}
```

This file must be created manually after running `cdk deploy`. The CDK outputs the values needed:

| CDK output | `appsettings.secrets.json` key |
|---|---|
| `ApiStack` → REST API URL | `ApiConfig.RestBaseUrl` |
| `WebSocketStack.WebSocketUrl` | `ApiConfig.WebSocketUrl` |

> **Note:** These URLs are infrastructure identifiers, not credentials. Access control is enforced by IAM resource policies and API Gateway authorizers on the backend.

