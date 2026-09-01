# Motorcito — Roadmap

**Model:** software-only (Path 1). No hardware sales. Recommend adapters, sell/ship the app.
**Target vehicles:** BMW M3 (G80) and Mazda MX-5 as dev cars; any 1996+ gas OBD-II vehicle as the eventual market.
**Platform:** .NET MAUI, Android first, iOS structurally supported from day one.

---

## Decided stack

| Layer | Choice | Why |
|---|---|---|
| Mobile app | .NET MAUI (C#) | Your strongest language; one codebase for both platforms |
| Local storage | SQLite | Must work with no connectivity; live gauges need instant reads |
| Adapter | OBDLink MX+ | STN chipset, ~100+ queries/sec, MFi-certified so iOS works |
| Second adapter | Cheap ELM327 clone (~$15) | Most users will own one; you must test against bad firmware |
| Raw archive | Azure Blob (Parquet, partitioned by vehicle/date) | Pennies/month; lets you reprocess after parser bugs |
| Sync/summary DB | Azure SQL serverless (auto-pause) | ~$5/mo, scales to zero when idle |
| Ingestion | Azure Functions (C#), consumption plan | Free tier covers your volume |
| Batch analysis | Azure Functions (Python) + DuckDB/pandas | Right-sized; no Spark, no Databricks |

**Multi-tenancy from commit one.** Every table gets `user_id` and `vehicle_id` even while you're the only user. Retrofitting this later is a rewrite.

**Capture VIN early.** Mode 09 PID 02 returns it. Year/make/model/engine is what makes cross-fleet baselines possible later — the only real moat this product has.

---

## Phase 0 — Setup and feasibility check (weekend 1)

Do this before writing meaningful code. It's cheap and it de-risks everything after.

- [ ] Order OBDLink MX+ and one cheap ELM327 clone
- [ ] Install .NET MAUI workload, confirm Android emulator + physical device deploy works
- [ ] Pair MX+ with phone over Bluetooth; connect with an existing app (Car Scanner / OBD Fusion) to confirm the hardware path works before your own code is in the mix
- [ ] **On each car, run `0100`, `0120`, `0140`, `0160`** — these return bitmasks of supported PIDs. Write down exactly which PIDs each vehicle actually exposes.
- [ ] Confirm fuel trims (`06`, `07`) and O2 (`14`) are present on both cars — if they aren't, the whole detection plan changes
- [ ] Read VIN via `0902`
- [ ] Set up git repo, Azure account (check free-tier credits)

**Gate:** if fuel trims aren't available on your dev cars, stop and re-plan before building anything.

---

## Phase 1 — Connection + live gauges (weeks 1–3)

Goal: open the app, see live RPM/coolant/speed moving. Immediately satisfying, proves the hardware path.

### Bluetooth layer
- [ ] Define `IObdAdapter` interface — `ConnectAsync`, `SendCommandAsync`, `DisconnectAsync`. **Everything else talks to this interface, never to Bluetooth directly.** This is what makes iOS/BLE support a swap rather than a rewrite.
- [ ] Implement Android Bluetooth Classic (SPP, UUID `00001101-0000-1000-8000-00805F9B34FB`)
- [ ] Device discovery + pairing UI
- [ ] Connection state machine: disconnected → connecting → connected → reconnecting. Cars turn off; handle it gracefully.

### ELM327 protocol
- [ ] Initialization sequence: `ATZ` (reset), `ATE0` (echo off), `ATL0` (no linefeeds), `ATS0` (no spaces), `ATSP0` (auto-detect protocol)
- [ ] Command/response loop with timeout + retry. Responses end with `>` prompt.
- [ ] Handle the junk: `NO DATA`, `SEARCHING...`, `UNABLE TO CONNECT`, `?`, `BUS INIT`, partial frames. **Clones return malformed data constantly — this is where most of your debugging time goes.**
- [ ] Parse Mode 01 responses: request `010C` → response `410C1AF8`

### PID decoding
- [ ] Build a PID registry (code, name, byte count, formula, unit). Start with:

| PID | Parameter | Formula | Unit |
|---|---|---|---|
| `04` | Engine load | `A*100/255` | % |
| `05` | Coolant temp | `A-40` | °C |
| `06` | STFT bank 1 | `(A-128)*100/128` | % |
| `07` | LTFT bank 1 | `(A-128)*100/128` | % |
| `08` | STFT bank 2 | `(A-128)*100/128` | % |
| `09` | LTFT bank 2 | `(A-128)*100/128` | % |
| `0B` | Intake MAP | `A` | kPa |
| `0C` | RPM | `(256A+B)/4` | rpm |
| `0D` | Speed | `A` | km/h |
| `0F` | Intake air temp | `A-40` | °C |
| `10` | MAF rate | `(256A+B)/100` | g/s |
| `11` | Throttle position | `A*100/255` | % |
| `14` | O2 S1 voltage | `A/200` | V |
| `1F` | Run time since start | `256A+B` | s |
| `2F` | Fuel level | `A*100/255` | % |
| `42` | Module voltage | `(256A+B)/1000` | V |
| `43` | Absolute load | `(256A+B)*100/255` | % |
| `5E` | Engine fuel rate | `(256A+B)/20` | L/h |

- [ ] Query `0100`/`0120`/`0140` at connect and **only poll PIDs the car actually supports** — asking for unsupported PIDs wastes your query budget

### Gauges
- [ ] Polling loop with configurable PID set and per-PID frequency (RPM fast, coolant slow)
- [ ] Gauge UI — start with plain numeric readouts, make them pretty later
- [ ] Show connection status and actual achieved sample rate (useful for debugging adapter throughput)

**Milestone:** live dashboard working on both cars, with both adapters.

---

## Phase 2 — Logging + local storage (weeks 3–5)

Start accumulating data as early as possible. Baselines need months; the clock starts when logging starts.

### Schema (SQLite, mirrored in Azure SQL)

```sql
-- vehicles
vehicle_id, user_id, vin, year, make, model, engine_code, nickname

-- trips
trip_id, vehicle_id, user_id, started_at, ended_at,
  distance_km, duration_s, ambient_temp_c, start_altitude_m

-- samples (wide format: one row per timestamp)
sample_id, trip_id, vehicle_id, ts,
  rpm, speed_kph, engine_load_pct, coolant_c, iat_c,
  stft1_pct, ltft1_pct, stft2_pct, ltft2_pct,
  maf_gps, map_kpa, throttle_pct, o2_s1_v, voltage_v, fuel_level_pct

-- events (DTCs and rule triggers)
event_id, vehicle_id, trip_id, ts, event_type, code,
  freeze_frame_json, window_start_ts, window_end_ts, severity, notes
```

Wide format over tall (`ts, pid, value`) — one row per timestamp is ~10x smaller and much faster to query for the bucketing work later. Index on `(vehicle_id, ts)`.

### Tasks
- [ ] Implement schema with migrations
- [ ] Trip detection: start on connect + RPM > 0; end on disconnect or sustained RPM = 0
- [ ] Buffered batch inserts (don't write every sample individually — batch 100–500)
- [ ] **Rolling in-memory buffer of last N minutes at full resolution** (target ~5 min). This is what you snapshot when an event fires.
- [ ] Downsample older in-trip data if storage becomes an issue (it won't — ~130 MB/year at 1 Hz)
- [ ] Trip list UI + basic trip detail view
- [ ] Log ambient temp at trip start (from IAT before warm-up, or a weather API)
- [ ] Log altitude from GPS — **altitude shifts fuel trims and will otherwise look like a fault**

**Milestone:** every drive logged automatically, browsable after the fact.

---

## Phase 3 — DTC reading + freeze-frame decoder (weeks 5–7)

This is your differentiated feature and it works on day one with zero baseline data.

- [ ] **Mode 03** — read stored DTCs. Response is pairs of bytes; decode to standard format (first 2 bits → P/C/B/U, then hex digits). `P0171`, `P0300`, etc.
- [ ] **Mode 07** — pending codes (failed once, not yet confirmed). Early warning, most apps ignore these.
- [ ] **Mode 0A** — permanent codes (can't be cleared by disconnecting battery)
- [ ] **Mode 02** — freeze frame. Same PID formulas as Mode 01, but prefix `02` and append frame number `00`: e.g. `020C00` for RPM at fault.
- [ ] DTC description database — bundle the generic SAE P0xxx list locally (public standard, ~2000 codes). Manufacturer-specific P1xxx codes are per-make; skip initially.
- [ ] **Mode 04** — clear codes. Put it behind a confirmation with a clear warning that it also erases readiness monitors.

### The part that makes this better than everything else on the market

- [ ] When a DTC is detected, capture **both**:
  - the ECU's freeze frame (works even if your app wasn't running)
  - **your own logged window** — 2 min before to 1 min after, from the rolling buffer
- [ ] Event detail screen: code + description + freeze-frame table + **a chart of the surrounding window** showing how conditions developed
- [ ] Historical context: "this code has set 3 times in 60 days, always above 70% load"
- [ ] Export event as PDF/CSV to hand to a mechanic

**Note:** most ECUs store only one freeze frame and overwrite it with the next fault. Your own window log is what fills that gap — that's the whole point.

**Milestone:** a fault produces a report that actually explains the circumstances, not just a code.

---

## Phase 4 — Cloud sync (weeks 7–9)

- [ ] Azure SQL serverless instance + schema
- [ ] Azure Blob container, path scheme `raw/{vehicle_id}/{yyyy}/{MM}/{dd}/{trip_id}.parquet`
- [ ] **Write raw Parquet before any transformation** — you will find a parser bug and need to reprocess
- [ ] Azure Function (C#) sync endpoint: accepts completed trips, writes Blob + SQL
- [ ] App-side sync queue: upload on WiFi, retry on failure, mark synced rows
- [ ] Hardcode `user_id` for now; leave the parameter in the API signature so auth slots in later
- [ ] Verify auto-pause resume latency doesn't make the app feel broken on cold start (cache aggressively on device)

**Milestone:** phone can be wiped without losing history.

---

## Phase 5 — Rules engine (weeks 9–12)

Runs **on the phone, in C#**, over recent SQLite data. No baseline needed — these come from published diagnostic practice.

### Exclusion filters (build these FIRST, they prevent most false positives)
- [ ] Drop samples in the first 30 s after engine start
- [ ] Drop samples during deceleration fuel cutoff (throttle closed + RPM > idle + speed dropping)
- [ ] Drop samples during hard acceleration (throttle delta above threshold)
- [ ] Drop samples while coolant < 70 °C for any rule that assumes closed-loop operation

### Rules
- [ ] **Fuel trim magnitude:** sustained `|STFT + LTFT| > 10%` → advisory; `> 25%` → warning (ECU near correction limit)
- [ ] **Fuel trim by load — the diagnostic gold:**
  - high positive at idle, normal at cruise → vacuum leak
  - positive across all loads → fuel delivery (pump/filter/injectors)
  - positive only at high load → fuel volume limit
  Compute trim averages separately for idle / cruise / high-load buckets and compare.
- [ ] **Thermostat:** coolant should reach 82–104 °C within 10–15 min of cold start. Plateau at 60–70 °C → stuck open. Gate on ambient temp.
- [ ] **O2 sensor lazy:** count voltage crossings through 0.45 V per second under warm closed-loop cruise. Healthy = 1–5 Hz. Below ~0.5 Hz → degraded.
- [ ] **Battery:** `< 12.4 V` at rest or `< 9.6 V` during crank → failing battery/charging system
- [ ] **Persistence requirement:** never alert on one sample or one trip. Require the condition across N consecutive trips.
- [ ] Rule triggers snapshot a window into `events` — same as DTCs. Catches things before the ECU considers them fault-worthy.

**Milestone:** app tells you something actionable without any historical baseline.

---

## Phase 6 — Bucketing + baseline accumulation (weeks 12–16, then wait)

Invisible work. Nothing user-facing ships here. This is what makes trends possible.

### Condition key
Every sample gets bucketed before aggregation:
- **Coolant band:** cold (<60 °C) / warming (60–82) / warm (>82)
- **Load band:** 0–20 / 20–40 / 40–60 / 60–80 / 80–100 %
- **RPM band:** idle / 1000–2000 / 2000–3000 / 3000+
- **Speed band:** stationary / city (<45 kph) / mixed (45–80) / highway (>80)

**Never compare raw values across different conditions.** "LTFT at warm/cruise/highway this month vs. three months ago" is valid. "LTFT overall" is meaningless.

- [ ] Per-bucket aggregation job (Python Azure Function, timer trigger, nightly): mean, stddev, sample count, per vehicle per bucket per week
- [ ] **Sufficiency gate:** a bucket isn't usable as a baseline until it has ≥500 samples across ≥10 separate trips
- [ ] Write baselines back to a summary table in Azure SQL; app pulls it on sync
- [ ] Design bucket keys so they can aggregate **across vehicles of the same year/make/model/engine** — this is the cross-fleet foundation

**Then drive normally for 2–3 months.** Don't try to build detection before the data exists.

---

## Phase 7 — Trend notifications (after ~3 months of data)

This is your "using more gas than usual" feature.

### Fuel economy calculation
From MAF: `L/h = (MAF_gps / 14.7 / 745) * 3600` (14.7 = stoich AFR, 745 g/L = gasoline density).
Then `km/L = speed_kph / L/h`. If PID `5E` (engine fuel rate) is supported, use it directly instead — more accurate.

**Critical:** only compare fuel economy within matched buckets. Highway cruise vs. highway cruise. A cold-weather city week vs. a warm highway week is not a comparison.

### Detection methods
- [ ] **Z-score on bucket means:** rolling 90-day baseline vs. trailing 7-day window. Flag at 2–3σ. Simple, interpretable, explainable to the user.
- [ ] **CUSUM for slow drift:** fuel trims creep, they don't jump. CUSUM is purpose-built for detecting small persistent shifts a threshold test misses. This is your "trims drifted 8% over three months" detector.
- [ ] **Linear regression on bucket means over time:** slope significantly ≠ 0 → "trending toward a problem" rather than "problem now"
- [ ] Confounder handling: log altitude (mountain drives shift trims), let the user tag fill-ups (ethanol content varies), factor ambient temperature

### Notification design
- [ ] **Always show the evidence, never just a verdict.** Chart of the trend, the bucket it fired on, the baseline range. A false positive the user can inspect is an annoyance; an unexplained one destroys trust.
- [ ] Conservative language: "worth having this inspected," never "your X has failed"
- [ ] Notification throttling — one alert per issue per week, not per trip
- [ ] Let the user dismiss/snooze a specific alert type

**Milestone:** app proactively tells you something is changing before a light comes on.

---

## Phase 8 — Optional / later

- [ ] **Load-weighted maintenance intervals** — oil life by engine hours and load, not odometer. 5,000 highway miles ≠ 5,000 short-cold-trip miles.
- [ ] **Catalyst monitoring** — ratio of downstream to upstream O2 variance; rising ratio = failing cat
- [ ] **Mode 06** — on-board test results with actual pass/fail margins. Very few consumer apps expose this.
- [ ] **iOS build** — MX+ works over MFi; the `IObdAdapter` abstraction should make this mostly UI work
- [ ] **BLE adapter support** — needed for iOS users who don't buy an MX+
- [ ] **Auth** (Entra External ID) + real multi-user
- [ ] **Isolation forest / one-class SVM** — only if you want the ML credential. It scores outlierness but won't tell you *what's* wrong, so it's strictly less useful than the rules for a maintenance product.
- [ ] **Cross-fleet baselines** — the actual moat. Once you have many same-model vehicles, new users get useful alerts on day one with no personal baseline period.

---

## Sequencing logic (why this order)

1. **Gauges first** — fastest path to something that works, proves the hardware chain, keeps you motivated
2. **Logging immediately after** — the baseline clock starts when logging starts, so start it early
3. **DTC/freeze-frame third** — differentiated, useful with zero data, ships while baselines accumulate
4. **Rules before statistics** — deterministic rules give real value without waiting months
5. **Trends last** — they're the headline feature but structurally require everything above

The mistake to avoid: building detection first. You'd be tuning thresholds against nothing.

---

## Known failure modes

| Risk | Mitigation |
|---|---|
| Cheap clones return garbage | Test against the clone from Phase 1, not at the end |
| False positives destroy trust | Exclusion filters + persistence requirement + always show evidence |
| Altitude/fuel changes look like faults | Log GPS altitude; let users tag fill-ups |
| Losing patience during the 3-month data wait | Phases 3 and 5 ship real features during that window |
| Car exposes fewer PIDs than expected | Phase 0 gate check before building anything |
| Bluetooth session drains phone battery | Consider a cheap dedicated Android device that lives in the car |

---

## Privacy note (matters even at one user, matters a lot at scale)

You'll be collecting vehicle identifiers, location, and driving behavior. Design for deletion as a hard requirement from the start. **Derive altitude from GPS and discard the coordinates** — you need altitude for trim normalization, you don't need a trace of where the car has been. That single decision dramatically reduces your regulatory exposure later.
