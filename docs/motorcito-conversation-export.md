# Motorcito — Project Conversation Export

*Export of the conversation that led to the Motorcito OBD-II telemetry app. Organized by topic rather than strict message order, with the reasoning and dead ends preserved.*

---

## Part 1 — Searching for a project idea

The conversation started with a request for app ideas that weren't already popular or done. Several batches were generated and checked. Most were already built.

### Ideas checked and rejected

| Idea | Verdict |
|---|---|
| "What's actually open" hours aggregator | Google/Yelp already do it at infrastructure level |
| EV charger real-time availability | PlugShare, ChargePoint, Tesla app all cover it |
| Subscription price-creep detector | Exists — Reclaim, Rocket Money both alert on price changes |
| Return-window tracker via email parsing | Exists — Orderly does exactly this |
| Cross-asset maintenance cost tracker | Exists — MaintainAll, Mainty |
| Personal price history via receipt OCR | Exists — Grocery Prices History, Receipts and Returns |
| Digital dead man's switch / estate packet | Exists — PostMortem, Vital Watchdog, Absentkey, Keeper |
| Bellwether-style acoustic fault detection | No consumer product, but IBM/LG hold patents; ADI OtoSense covers industrial |

### Ideas that survived vetting

- **Automated scope-creep detector for freelancers** — market has manual Notion templates and email scripts, nothing that parses client conversations and flags out-of-scope asks
- **Regulatory change watcher per hobby** — B4UFly tells you where you can fly now, nothing pushes "your state's e-bike rules changed"
- **Acoustic fault detection for home machines** — technique proven industrially, no consumer product, but patent-encumbered

### Lesson from this phase

Generating ideas from instinct and checking afterward has a poor hit rate. Obvious-sounding ideas are exactly the ones someone already built. Also: "not yet invented" is a very high bar — the realistic target is "exists but badly executed, or serves an adjacent niche."

The spaces that stayed genuinely open were the ones where the work is unglamorous — parsing documents, tracking legislation, normalizing messy data — not where the concept is clever.

---

## Part 2 — Landing on the OBD-II project

### The original idea

An app that alerts when the car is developing anomalies — burning oil, using more gas than usual.

### What already exists

OBD-II apps are plentiful but they're all **display and logging** tools:
- inCarDoc — real-time monitoring, charts, fault codes, maintenance logbook
- OBD Fusion — codes, custom dashboards, fuel economy estimates
- Torque, Car Scanner, OBDeleven, MotorData, DashCommand, OBD Auto Doctor, OBDocker, OnTrack

None do **longitudinal baselining** — "your fuel trims have drifted 8% over three months, something is changing." That's the open gap.

### The important technical catch

**OBD-II does not expose oil consumption.** There's no standard PID for oil level or quantity. Real oil-consumption detection requires a dedicated oil quantity sensor feeding a manufacturer-level prediction model. Not available from a $30 ELM327 dongle. Some cars expose oil level over manufacturer-specific CAN, but that's per-make reverse engineering.

### What you *can* baseline from standard PIDs

- **Fuel trims (short and long term)** — the goldmine. Early warning of vacuum leaks, injector issues, aging O2 sensors. Almost no consumer app tracks these over time.
- Fuel economy trend at comparable speed/load
- Coolant temp behavior and warm-up time
- O2 sensor switching frequency
- Catalyst efficiency via Mode 06
- Battery voltage at rest and cranking

---

## Part 3 — Feasibility assessment

Overall: high likelihood of building it, lower likelihood the anomaly detection is trustworthy. Roughly 90% vs 40%.

| Component | Difficulty | Notes |
|---|---|---|
| Live gauges | ~95% | Solved problem, lots of prior art |
| Adapter connectivity | Moderate | Platform-specific gotcha — see below |
| Mobile app layer | ~80% | MAUI keeps you in C#; Bluetooth is new ground |
| Backend pipeline | ~95% | Your day job |
| Anomaly detection that works | ~40% | The real risk |

### The Bluetooth platform gotcha

- **Android**: ELM327 clones use Bluetooth Classic SPP, natively supported. Cheap adapters just work.
- **iOS**: Bluetooth Classic requires MFi certification, which no $20 clone has. Restricted to BLE adapters or WiFi adapters (and WiFi adapters take over the interface, killing internet).

### Why detection is the hard part

You cannot build detection until you have months of baseline data. Fuel trims vary legitimately with altitude, ambient temp, fuel batch, load, and warm-up state. Comparing a cold-start city commute against a warm highway run generates constant false alarms.

The normalization work — bucketing by coolant temp, engine load, and speed band before comparing anything — is the actual intellectual content of the project.

---

## Part 4 — Fallback features if detection underperforms

All of these work with zero baseline data:

1. **Freeze-frame decoder and DTC context** (highest value, lowest risk) — every app shows `P0171` and a generic definition. None show what was happening: RPM, load, coolant temp, fuel trims at the moment of the fault, plus your own historical context.
2. **Trip-level reports with condition segmentation** — break each drive into cold-start, warm-up, city, highway segments. Existing apps give one blended number, which is nearly meaningless.
3. **Deterministic threshold rules** — fuel trims beyond ±10% sustained, coolant not reaching operating temp, O2 stuck at fixed voltage, low battery voltage. Established mechanic heuristics, no learning required.
4. **Load-weighted maintenance intervals** — oil life by engine hours and load, not odometer.
5. **Warm-up and cold-start analysis** — coolant rise rate is a reliable thermostat indicator with few confounders.
6. **Export/shareability** — clean PDF or CSV of a fault event with freeze-frame context to hand a mechanic.

### The two capture mechanisms

- **ECU freeze frame (Mode 02)** — the car records it whether or not your app was running. But most ECUs store only one, overwritten by the next fault, and it's a single instant.
- **Your own rolling log** — the full time series around the event. "Coolant climbed steadily for 90 seconds before the code set while load stayed constant" tells a mechanic far more.

They complement each other. Implement both.

---

## Part 5 — Stack decisions

### Data volume math

10 PIDs at 1 Hz, one hour of driving per day:
- ~360 KB per driving hour
- **~130 MB per year** in a relational table
- Even aggressive sampling lands around 2 GB/year raw, under 400 MB as compressed Parquet

### Why Databricks was the wrong call

Databricks was suggested early and it was a mistake — reached for because it's on the résumé, not because it fit. It bills for cluster uptime, not data volume. A minimal cluster runs $0.30–0.75/hour; leave one running and you're at $200–500/month for a dataset that fits in RAM.

Using distributed compute on 100 MB is a judgment red flag, not a credential.

### Final stack

| Layer | Choice |
|---|---|
| Mobile app | .NET MAUI (C#), Android first |
| Local storage | SQLite |
| Raw archive | Azure Blob (Parquet, partitioned by vehicle/date) |
| Sync/summary DB | Azure SQL serverless (auto-pause) |
| Ingestion | Azure Functions (C#), consumption plan |
| Batch analysis | Azure Functions (Python) + DuckDB/pandas |

Cost: under $10/month. Storage is negligible; the only real cost decision is whether you run an always-on database.

### Where things run

- **Layer 1 rules** (fuel trims, thermostat, O2, battery) — on the phone in C#, over local SQLite. Works offline, alerts in real time.
- **Layers 2–3** (bucketing, baselines, CUSUM, drift) — Python, off-device. Timer-triggered Azure Function, nightly or weekly. Results written back as a small summary table the phone pulls on sync.

---

## Part 6 — Anomaly detection method

### Layer 1: Deterministic rules (ship immediately)

**Fuel trim rules — the most valuable signal.**

STFT (PID `06`/`08`) and LTFT (`07`/`09`) show how much the ECU is correcting away from its base map. Zero = map is right. Positive = adding fuel (running lean).

- Sustained combined trim beyond ±10% → something's off
- Beyond ±25% → ECU near correction limit, code coming
- **The diagnostic gold is idle vs. cruise comparison:**
  - High positive at idle, normal at cruise → vacuum leak
  - Positive across all loads → fuel delivery (pump/filter/injectors)
  - Positive only at high load → fuel volume limit

**Thermostat:** coolant should reach 82–104°C within 10–15 min of cold start. Plateau at 60–70°C → stuck open. Gate on ambient.

**O2 sensor:** upstream voltage should oscillate 0.1–0.9V several times per second under closed-loop cruise. Healthy = 1–5 Hz crossings. Below ~0.5 Hz → degraded.

**Catalyst:** downstream O2 should be flat relative to upstream. Rising variance ratio → failing cat.

**Battery:** below ~12.4V at rest or ~9.6V during crank.

### Layer 2: Conditional baselining

Never compare raw values. Bucket every sample by:
- Coolant band: cold (<60°C) / warming (60–82) / warm (>82)
- Load band: 20% increments
- RPM band: idle / 1000–2000 / 2000–3000 / 3000+
- Speed band: stationary / city / mixed / highway

Only compare like against like. Require ≥500 samples across ≥10 trips before a bucket's baseline is usable.

### Layer 3: Statistical drift (after ~3 months)

- **Z-score on bucket means** — 90-day baseline vs 7-day window, flag at 2–3σ
- **CUSUM** — purpose-built for small persistent shifts a threshold test misses. This is the "trims drifted 8% over three months" detector.
- **Linear regression on bucket means** — significant non-zero slope = trending toward a problem

### Layer 4: ML (optional)

Isolation forest or one-class SVM on normal-operation feature vectors. Honest unsupervised detection, works with only normal data. But it produces a score, not a diagnosis — strictly less useful than the rules for a maintenance product. Include it for the credential, not the product.

### False positive guardrails

- Require persistence across N consecutive trips
- Gate on data sufficiency
- Exclude transients: first 30s after start, hard acceleration, deceleration fuel cutoff
- Log GPS altitude (mountain drives shift trims), let users tag fill-ups (ethanol varies)
- **Always show the evidence, not just a verdict**

---

## Part 7 — Fuel economy calculation

From MAF: `L/h = (MAF_gps / 14.7 / 745) * 3600` where 14.7 is stoich AFR and 745 g/L is gasoline density.

Then `km/L = speed_kph / L/h`. If PID `5E` (engine fuel rate) is supported, use it directly.

Only compare within matched buckets. Highway cruise vs highway cruise.

---

## Part 8 — Hardware

### Adapter: OBDLink MX+

- Most capable consumer Bluetooth OBD2 adapter as of 2026, universal platform support, broad app compatibility
- Free access to J1850, MS-CAN, SW-CAN protocols
- MFi certified — the only way to get Bluetooth Classic throughput on iPhone
- STN chipset, 100+ queries/sec vs ~5–15/sec from clones
- Supports enhanced diagnostics for manufacturer-specific data
- ~$100–140

### Rejected: OBDLink CX

OBDLink's own support states it's optimized for BMW and **will not work on GM, Ford, and certain FCA vehicles built before 2008**. Narrow app support outside BimmerCode. Wrong tool for a general-purpose custom app.

### Also buy: a cheap ELM327 clone (~$15)

Not instead of — in addition. Most users will own clones. Clones return partial/corrupted frames, respond inconsistently, misreport firmware version, drop the `>` prompt, echo when told not to, choke above ~10 queries/sec. If your parser was written against clean input, every one of those becomes a field crash.

Buy it when you start hardening the protocol layer, not week one.

### PID coverage reality

**Near-universal:** RPM (`0C`), speed (`0D`), coolant (`05`), load (`04`), throttle (`11`), IAT (`0F`)

**Common but not guaranteed:** MAF (`10`) — speed-density cars use MAP (`0B`) instead; fuel trims — bank 2 only on V engines; O2 (`14`) — wideband cars report differently via `24`–`2B`

**Uneven:** 1996–2000 cars support a bare minimum and use older protocols. 2008+ all CAN-based and consistent. European models sometimes expose less. Diesels use different PIDs entirely.

**Therefore:** query `0100`/`0120`/`0140`/`0160` at every connection and build the gauge layout dynamically. Degrade gracefully — tell the user "your vehicle doesn't report MAF, so fuel economy is estimated from MAP."

---

## Part 9 — Commercialization path

**Chosen: Path 1 — software only.** Ship the app, recommend adapters, possibly take affiliate revenue. Zero inventory, zero certification, zero RMA burden. This is what Car Scanner, OBD Fusion, and Torque all do.

**Rejected paths:**
- *White-label an existing adapter* — MOQs 500–1,000 units, $8–20/unit, plus FCC certification, CE, and Bluetooth SIG qualification. Branding a clone board ties your reputation to hardware you didn't design.
- *License an STN chipset* — OBD Solutions sells them via OEM program. Real hardware engineering, tens of thousands upfront.

**Why software first:** hardware turns a software business into a logistics business, consumes capital, iterates slowly, and a bad batch can end the company. Your differentiator is the analysis layer and eventually the fleet baseline data — none of which requires owning the dongle.

### What changes if you commercialize

- **iOS becomes mandatory** — plan a hardware abstraction layer so adapter support is pluggable
- **Multi-tenancy from the first commit** — every table gets `user_id` and `vehicle_id`. Retrofitting is a rewrite.
- **Capture VIN early** (Mode 09 PID 02) — this enables cross-fleet baselines
- Privacy: derive altitude from GPS and **discard coordinates**. You need altitude for normalization, not a location trace.
- Liability: frame as "worth having inspected," never "your X has failed"

### The actual moat

Not the app — anyone can poll PIDs. **The fleet baseline data.** Once a thousand same-model cars report normal trim distributions, you can tell a new user "your trims are abnormal for your specific model" on day one with no baseline period. That compounds.

---

## Part 10 — The naming process

Six names were checked and blocked before landing on one.

| Name | Result |
|---|---|
| EngineIQ | engineiq.io (deadpooled), Engine IQ Pty Ltd (finance, active). "IQ" suffix crowded — AutoIQ is a close competitor |
| MotorSense | Conflicts with ADI OtoSense + "Smart Motor Sensor," both machine-health monitoring |
| ColdStart | **Serial 99089404 — COLD START — LIVE REGISTERED, Class 009/041, automotive podcasts, Flying Husky Racing LLC.** Identical mark, same class, same field. |
| EngineTrend | Too descriptive to register (Section 2(e)(1) refusal likely) |
| Murmur | **Serial 87240988 — LIVE REGISTERED, Class 009/045, downloadable mobile applications.** Plus MURMMOR pending in 009/042. |
| EngineWatch | ENGINEWATCH itself is dead, but ENGINE is live in 009 twice and WATCH in 009 is held by **Apple** |
| Bellwether | **Serial 98504776 — LIVE PENDING, Class 009/035/042, downloadable software, X Development LLC (Google).** Plus Bellwether Electronic in 009. |

### Lesson

Class 009 is one of the busiest classes at the USPTO. Every evocative real word is claimed. "Uncommon word" and "unclaimed in Class 009" turned out to be different things.

### Final: **Motorcito**

**USPTO: 0 results.**

Spanish diminutive of "motor" — "little engine." Works because:
- Real construction, sounds natural rather than committee-invented
- Warm and friendly where competitors are clinical
- Says "engine" without being descriptive in English — the registrable sweet spot
- Spanish-language names in Class 009 are far less crowded

**Full listing:** Motorcito — OBD2 Engine Trend Monitor

**App Store keyword field:** OBDII, ELM327, scanner, diagnostic, DTC, fault codes, car health, freeze frame

**Still to verify:** App Store and Play Store direct search (common-law rights don't require registration), and grab motorcito.app.

---

## Appendix — Positioning note

The niche is **failure prediction / trend detection**, not diagnostics. The market is saturated with scanners. Leading with "OBD2 Scanner" puts you head-on against Torque and Car Scanner for the same search term and makes you look like one more of them.

But avoid "predict" in user-facing copy — it invites liability. Use "detect," "track," "monitor," "spot." Same product, very different exposure.

In-app framing: "your fuel trims have drifted from baseline, worth having this inspected" is a statement of observed fact plus a suggestion. Safer and more honest than a failure prediction, and it's what the data actually supports.
