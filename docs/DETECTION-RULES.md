# Detection Rules & Statistical Methods

Three layers. Build them in order. **Do not attempt Layer 3 before months of real data exist** — you'd be tuning thresholds against nothing.

---

## Exclusion filters — build these FIRST

These prevent most false positives. Apply before any rule evaluates or any sample enters a baseline bucket.

| Filter | Condition | Why |
|---|---|---|
| Cold start transient | First 30s after engine start | ECU is in open loop, trims meaningless |
| Open loop | Coolant < 70°C | Fuel control not yet closed loop |
| Deceleration fuel cutoff | Throttle closed + RPM > idle + speed decreasing | Injectors off, trims spike |
| Hard acceleration | Throttle delta above threshold | Enrichment mode, not representative |
| Insufficient data | Bucket has < 500 samples or < 10 distinct trips | Baseline not yet meaningful |

---

## Layer 1 — Deterministic rules

Runs on-device in C# over recent SQLite data. No baseline required. These come from published diagnostic practice.

### Fuel trims — the most valuable signal

STFT (`06`/`08`) and LTFT (`07`/`09`) show how far the ECU is correcting from its base fuel map.
- Zero = map is correct
- Positive = adding fuel (engine running lean)
- Negative = removing fuel (running rich)

**Magnitude thresholds:**

| Combined STFT + LTFT | Severity |
|---|---|
| beyond ±10% sustained | Advisory |
| beyond ±25% | Warning — ECU near correction limit, DTC likely soon |

**The load comparison — this is the diagnostic gold.**

Compute trim averages separately for idle, cruise, and high-load buckets, then compare:

| Pattern | Likely cause |
|---|---|
| High positive at idle, normal at cruise | Vacuum leak (unmetered air matters proportionally more at low airflow) |
| Positive across all loads | Fuel delivery — weak pump, clogged filter, failing injectors |
| Positive only at high load | Fuel volume limit |
| Negative across the board | Excess fuel — leaking injector, high fuel pressure, failing MAF reading low |

No competitor does this comparison. It's pure arithmetic over data you already have.

### Thermostat

Coolant should reach 82–104°C within 10–15 minutes of a cold start. A plateau at 60–70°C indicates a thermostat stuck open.

Gate on ambient temperature — use IAT at startup (before the engine warms the intake) or PID `46` if supported.

### O2 sensor lazy detection

Under warm closed-loop cruise, upstream O2 voltage should oscillate through 0.45V several times per second.

- Count crossings per second
- Healthy: 1–5 Hz
- Below ~0.5 Hz: degraded sensor

Only evaluate during steady cruise — transients invalidate the count.

### Catalyst efficiency

Downstream O2 should be relatively flat compared to upstream. Compute:

```
ratio = variance(downstream_o2) / variance(upstream_o2)
```

A rising ratio over time indicates falling catalyst efficiency. This is a trend metric — it belongs in Layer 3 for alerting, but the computation lives here.

### Battery and charging

| Condition | Meaning |
|---|---|
| `42` < 12.4V at rest | Battery low or failing |
| `42` < 9.6V during crank | Battery failing under load |
| `42` > 15V running | Overcharging — voltage regulator |

### Persistence requirement

**Never alert on a single sample or a single trip.** Require the condition to hold across N consecutive trips (start with N=3). Store rule evaluations per trip and evaluate the streak.

---

## Layer 2 — Conditional baselining

Invisible to the user. Nothing ships here. This is what makes Layer 3 possible.

### The core principle

**Never compare raw values across different operating conditions.** Fuel trims, fuel economy, and temperatures all vary legitimately with load, speed, and warm-up state. Comparing a cold city commute to a warm highway run generates constant false alarms.

### Condition key

Every sample is bucketed on four dimensions:

| Dimension | Bands |
|---|---|
| Coolant | cold (<60°C) / warming (60–82) / warm (>82) |
| Engine load | 0–20 / 20–40 / 40–60 / 60–80 / 80–100 % |
| RPM | idle / 1000–2000 / 2000–3000 / 3000+ |
| Speed | stationary / city (<45 kph) / mixed (45–80) / highway (>80) |

That yields a few hundred buckets. Most will be sparse; that's expected.

### Aggregation

Nightly Python job computes per bucket, per vehicle, per week:
- mean
- standard deviation
- sample count
- distinct trip count

Write results to a summary table in Azure SQL. The app pulls this on sync.

### Sufficiency gate

A bucket is not usable as a baseline until it has **≥500 samples across ≥10 distinct trips.** Below that, trending on it is trending on noise.

### Cross-fleet design

Bucket keys must be aggregatable across vehicles of the same year/make/model/engine, not just within one car. This is the foundation of the eventual moat: telling a new user "your trims are abnormal for a 2021 G80" on day one, with no personal baseline period.

Capture VIN (Mode 09 PID 02) from the start.

---

## Layer 3 — Statistical drift detection

Requires ~3 months of accumulated data. Runs in Python off-device.

### Z-score on bucket means

For each bucket, maintain a rolling 90-day baseline. Compare the trailing 7-day window against it.

```
z = (recent_mean - baseline_mean) / baseline_stddev
```

Flag at |z| > 2–3. Simple, interpretable, and you can explain exactly why it fired.

### CUSUM — for slow drift

Fuel trims creep; they don't jump. A threshold test misses a gradual 8% shift over three months. CUSUM (cumulative sum control chart) is purpose-built for detecting small persistent shifts.

```
S_high[i] = max(0, S_high[i-1] + (x[i] - target) - k)
S_low[i]  = max(0, S_low[i-1] - (x[i] - target) - k)
```

Alarm when either exceeds decision limit h. Typical starting values: k = 0.5σ, h = 5σ. Tune against real data.

This is the primary detector for "your trims have drifted."

### Linear regression on bucket means

Fit a slope over time per bucket. Flag statistically significant non-zero slopes. Produces "trending toward a problem" rather than "problem now" — more actionable, earlier.

---

## Confounders that will cause false positives

| Confounder | Mitigation |
|---|---|
| **Altitude** — shifts fuel trims meaningfully | Log GPS altitude. Derive it and **discard coordinates** (privacy). Bucket or correct for it. |
| **Fuel ethanol content** — varies by batch and station | Let the user tag fill-ups. Treat a fill-up as a potential discontinuity. |
| **Ambient temperature** | Log at trip start; required for thermostat rules |
| **Seasonal fuel blends** | Winter/summer formulations shift trims. Long baselines partially absorb this. |
| **Recent service** | Let the user log maintenance; reset or re-baseline affected buckets |

---

## Alerting design

**Always show the evidence, never just a verdict.** Include:
- the trend chart
- which bucket fired
- the baseline range vs the recent value
- how many trips the condition has persisted

A false positive the user can inspect is a minor annoyance. An unexplained one destroys trust in the whole app.

**Language rules (liability):**
- Say: "worth having this inspected," "this has changed from your baseline," "trending outside normal range"
- Never say: "your catalytic converter is failing," "predicted failure," "your X will fail"

**Throttling:** one alert per issue per week, not per trip. Let users dismiss or snooze by alert type.

---

## Event capture

Both DTCs and Layer 1 rule triggers should snapshot a window from the rolling buffer.

- **Rolling buffer:** keep the last ~5 minutes at full resolution in memory
- **On trigger:** persist 2 minutes before to 1 minute after, tag as an event
- **Also fetch the ECU freeze frame** (Mode 02) — it exists even if the app wasn't running

Rule triggers capturing windows is where the early-warning value lives: it records events the ECU doesn't yet consider fault-worthy.

---

## Layer 4 — ML (optional, low priority)

If an ML component is wanted for portfolio reasons: isolation forest or one-class SVM trained on normal-operation feature vectors, scoring new trips for outlierness. Honest unsupervised detection that works with only normal data.

**But be clear-eyed:** it produces a score, not a diagnosis. The rule layer produces actionable causes. For a maintenance product the rules are strictly more useful. Add ML to demonstrate the skill, not to improve the product.
