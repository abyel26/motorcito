# Schema

Mirrored between SQLite (on device) and Azure SQL (sync target). Keep them structurally identical so sync is a straight copy.

**Every table carries `user_id` and `vehicle_id`.** There is currently one user. This is not negotiable — retrofitting multi-tenancy is a rewrite.

---

## Why wide, not tall

Samples are stored **one row per timestamp** with a column per PID, not `(ts, pid, value)` triples.

- ~10x smaller on disk
- Vastly faster for the bucketing queries in Layer 2, which need all PIDs at a given instant
- Sparse columns (unsupported PIDs) cost almost nothing in SQLite

Trade-off: adding a new PID means a migration. Acceptable — the PID set is stable.

---

## Tables

### vehicles

```sql
CREATE TABLE vehicles (
    vehicle_id      TEXT PRIMARY KEY,
    user_id         TEXT NOT NULL,
    vin             TEXT,
    year            INTEGER,
    make            TEXT,
    model           TEXT,
    engine_code     TEXT,
    nickname        TEXT,
    supported_pids  TEXT,           -- JSON array from 0100/0120/0140/0160
    created_at      TEXT NOT NULL   -- UTC ISO8601
);
```

`vin`, `year`, `make`, `model`, `engine_code` are what enable cross-fleet baselines later. Capture VIN via Mode 09 PID 02 at first connection.

### trips

```sql
CREATE TABLE trips (
    trip_id           TEXT PRIMARY KEY,
    vehicle_id        TEXT NOT NULL,
    user_id           TEXT NOT NULL,
    started_at        TEXT NOT NULL,
    ended_at          TEXT,
    distance_km       REAL,
    duration_s        INTEGER,
    ambient_temp_c    REAL,         -- IAT at start, or PID 46
    start_altitude_m  REAL,         -- from GPS; coordinates NOT stored
    was_cold_start    INTEGER,      -- 0/1
    synced            INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (vehicle_id) REFERENCES vehicles(vehicle_id)
);

CREATE INDEX idx_trips_vehicle_started ON trips(vehicle_id, started_at);
```

**Privacy:** store derived altitude only. Never persist latitude/longitude — altitude is all the normalization needs, and dropping coordinates dramatically reduces regulatory exposure.

Trip detection: start on connect + RPM > 0. End on disconnect or sustained RPM = 0.

### samples

```sql
CREATE TABLE samples (
    sample_id       INTEGER PRIMARY KEY AUTOINCREMENT,
    trip_id         TEXT NOT NULL,
    vehicle_id      TEXT NOT NULL,
    ts              TEXT NOT NULL,   -- UTC ISO8601

    rpm             REAL,
    speed_kph       REAL,
    engine_load_pct REAL,
    abs_load_pct    REAL,
    coolant_c       REAL,
    iat_c           REAL,
    stft1_pct       REAL,
    ltft1_pct       REAL,
    stft2_pct       REAL,
    ltft2_pct       REAL,
    maf_gps         REAL,
    map_kpa         REAL,
    throttle_pct    REAL,
    timing_adv_deg  REAL,
    o2_s1_v         REAL,
    o2_s2_v         REAL,
    lambda_cmd      REAL,
    voltage_v       REAL,
    fuel_level_pct  REAL,
    fuel_rate_lph   REAL,
    runtime_s       INTEGER,

    FOREIGN KEY (trip_id) REFERENCES trips(trip_id)
);

CREATE INDEX idx_samples_vehicle_ts ON samples(vehicle_id, ts);
CREATE INDEX idx_samples_trip ON samples(trip_id);
```

Insert in batches of 100–500. Never write per sample.

### events

DTCs and rule triggers both land here.

```sql
CREATE TABLE events (
    event_id          TEXT PRIMARY KEY,
    vehicle_id        TEXT NOT NULL,
    user_id           TEXT NOT NULL,
    trip_id           TEXT,
    ts                TEXT NOT NULL,
    event_type        TEXT NOT NULL,   -- 'dtc' | 'pending_dtc' | 'permanent_dtc' | 'rule'
    code              TEXT,            -- e.g. 'P0171', or rule id like 'trim_lean_idle'
    severity          TEXT,            -- 'info' | 'advisory' | 'warning'
    freeze_frame_json TEXT,            -- ECU Mode 02 snapshot
    window_start_ts   TEXT,            -- our own logged window
    window_end_ts     TEXT,
    notes             TEXT,
    acknowledged      INTEGER NOT NULL DEFAULT 0,
    synced            INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_events_vehicle_ts ON events(vehicle_id, ts);
```

Two capture sources, both worth keeping:
- `freeze_frame_json` — the ECU's own snapshot, exists even if the app wasn't running, but it's one instant and gets overwritten by the next fault
- `window_start_ts`/`window_end_ts` — pointers into `samples` for the surrounding 2-min-before / 1-min-after window from the rolling buffer

### baselines

Computed off-device, pulled down on sync. Read-only on the phone.

```sql
CREATE TABLE baselines (
    baseline_id     TEXT PRIMARY KEY,
    vehicle_id      TEXT NOT NULL,
    user_id         TEXT NOT NULL,
    metric          TEXT NOT NULL,   -- 'ltft1_pct', 'fuel_econ_kmpl', etc.
    coolant_band    TEXT NOT NULL,   -- 'cold' | 'warming' | 'warm'
    load_band       TEXT NOT NULL,   -- '0-20' ... '80-100'
    rpm_band        TEXT NOT NULL,   -- 'idle' | '1000-2000' | '2000-3000' | '3000+'
    speed_band      TEXT NOT NULL,   -- 'stationary' | 'city' | 'mixed' | 'highway'
    window_start    TEXT NOT NULL,
    window_end      TEXT NOT NULL,
    mean_value      REAL NOT NULL,
    stddev_value    REAL,
    sample_count    INTEGER NOT NULL,
    trip_count      INTEGER NOT NULL,
    is_sufficient   INTEGER NOT NULL  -- sample_count >= 500 AND trip_count >= 10
);

CREATE INDEX idx_baselines_lookup
    ON baselines(vehicle_id, metric, coolant_band, load_band, rpm_band, speed_band);
```

Never evaluate a trend against a baseline where `is_sufficient = 0`.

### maintenance_log

User-entered. Needed because service resets baselines.

```sql
CREATE TABLE maintenance_log (
    entry_id     TEXT PRIMARY KEY,
    vehicle_id   TEXT NOT NULL,
    user_id      TEXT NOT NULL,
    performed_at TEXT NOT NULL,
    category     TEXT,      -- 'oil' | 'plugs' | 'filter' | 'fuel_fillup' | 'other'
    odometer_km  REAL,
    notes        TEXT
);
```

Fill-ups matter: ethanol content varies by batch and shifts fuel trims. Treat a fill-up as a potential discontinuity in trend analysis.

### sync_queue

```sql
CREATE TABLE sync_queue (
    queue_id      INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type   TEXT NOT NULL,   -- 'trip' | 'event'
    entity_id     TEXT NOT NULL,
    user_id       TEXT NOT NULL,
    attempts      INTEGER NOT NULL DEFAULT 0,
    last_attempt  TEXT,
    last_error    TEXT
);
```

Upload on WiFi. Retry with backoff. Mark `synced = 1` on the source row only after confirmation.

`user_id` is here for the same reason it is on every other table. Without it, erasing a user would mean resolving each queued `entity_id` back to its owner through tables that the erasure had already emptied — so queued rows would survive a deletion request.

---

## Blob layout

Raw Parquet, written before any transformation:

```
raw/{vehicle_id}/{yyyy}/{MM}/{dd}/{trip_id}.parquet
```

You will find a parser bug and need to reprocess. Raw history is unrecoverable if not kept.

---

## Retention

At 1 Hz with 10 PIDs, roughly 130 MB/year per vehicle. **Storage is not the constraint** — that much is affordable for years.

**Necessity is the constraint.** Once data is synced it is personal data, and GDPR's storage-limitation principle asks how long it is needed for the stated purpose, not how long it fits. "We had the disk space" is not an answer. So retention is a decision about what the analysis actually requires:

- **Raw 1 Hz samples** matter while baselines are being established and while a parser bug might mean reprocessing. Past that, the value is mostly in the aggregates already computed from them.
- **Event windows are never downsampled.** Their whole purpose is showing how conditions developed around a fault at full resolution.
- **Baselines are small and permanent.** They are the product's memory of the vehicle.

Practical policy: keep raw samples at full resolution for 6 months, then downsample to 0.2 Hz, retaining event windows untouched. This satisfies storage limitation and happens to shrink sync payloads and speed up queries.

If the device runs tight sooner, apply the same downsampling earlier on the phone while keeping full resolution server-side.

---

## Two data tiers

A distinction that must survive into the sync design, because retrofitting it is expensive.

**Tier 1 — the user's data.** Everything in this schema: vehicles, trips, samples, events, baselines, maintenance. Keyed to `user_id`, and therefore personal data. Fully erasable — see `DataErasure.EraseUser`, which is tested to leave nothing behind in any table.

**Tier 2 — the fleet model.** Aggregate statistics that answer "is this normal for a car like yours": distributions per make, model, engine and mileage band. **No `user_id`, no `vehicle_id`, no VIN, no device identifier**, and a minimum cohort size per bucket so no individual can be singled out. Genuinely anonymous, and so outside GDPR.

Why the split matters: **without it, every erasure request damages the analysis model.** With it, a user deletes their tier-1 data in full while the fleet model is untouched, because their contribution was aggregated beyond re-identification the moment it arrived.

### VIN handling

The plaintext VIN is useful on-device — it decodes year, make, model and engine — so it stays in the local `vehicles` table. **Nothing beyond the device needs it.** A VIN identifies a specific vehicle and, through registration and insurance records, its owner.

Anything leaving the device carries `VinHasher.Hash` output instead: an HMAC-SHA256 under a server-held secret. A plain hash would not do — the VIN space is 17 structured characters from a restricted alphabet, small enough to brute-force — so the secret is what makes it non-reversible, and it must never ship in an app binary. The intended flow is that the device sends plaintext over TLS, the server hashes on arrival and discards the original.
