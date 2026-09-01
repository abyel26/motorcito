# PID Reference & ELM327 Protocol

## ELM327 initialization sequence

Send in order at connection. Each command returns a response terminated by the `>` prompt.

| Command | Purpose |
|---|---|
| `ATZ` | Reset adapter |
| `ATE0` | Echo off |
| `ATL0` | Linefeeds off |
| `ATS0` | Spaces off in responses |
| `ATSP0` | Auto-detect protocol |

## Request/response format

Request a Mode 01 PID by concatenating mode and PID: `010C` for RPM.

Response echoes mode+0x40 then the PID then data bytes: `410C1AF8`
- `41` = mode 01 response
- `0C` = PID
- `1AF8` = data bytes A=0x1A, B=0xF8
- RPM = (256×26 + 248) / 4 = **1728 rpm**

## Junk the parser must handle

Cheap clones emit all of these. None should crash or produce a bad reading:

- `NO DATA` — PID not supported or no response in time
- `SEARCHING...` — protocol detection in progress
- `UNABLE TO CONNECT` — no bus communication
- `BUS INIT: ...` — initialization chatter
- `?` — command not understood
- `STOPPED` — interrupted
- `CAN ERROR`, `BUFFER FULL`
- Partial frames, missing `>` prompt, spaces despite `ATS0`, echo despite `ATE0`
- Multi-line responses for multi-frame data

**Strategy:** validate every response against expected mode+PID prefix and expected byte count before parsing. Reject and retry on mismatch. Never trust length alone.

---

## Capability discovery

Query these at every connection to learn which PIDs the ECU actually supports. Each returns a 32-bit bitmask.

| Command | Covers |
|---|---|
| `0100` | PIDs 01–20 |
| `0120` | PIDs 21–40 |
| `0140` | PIDs 41–60 |
| `0160` | PIDs 61–80 |

Only poll supported PIDs. Querying unsupported ones wastes the query budget and returns `NO DATA`.

---

## Mode 01 — live data

| PID | Parameter | Bytes | Formula | Unit |
|---|---|---|---|---|
| `04` | Calculated engine load | 1 | `A*100/255` | % |
| `05` | Engine coolant temp | 1 | `A-40` | °C |
| `06` | Short term fuel trim, bank 1 | 1 | `(A-128)*100/128` | % |
| `07` | Long term fuel trim, bank 1 | 1 | `(A-128)*100/128` | % |
| `08` | Short term fuel trim, bank 2 | 1 | `(A-128)*100/128` | % |
| `09` | Long term fuel trim, bank 2 | 1 | `(A-128)*100/128` | % |
| `0B` | Intake manifold pressure | 1 | `A` | kPa |
| `0C` | Engine RPM | 2 | `(256A+B)/4` | rpm |
| `0D` | Vehicle speed | 1 | `A` | km/h |
| `0E` | Timing advance | 1 | `A/2-64` | ° before TDC |
| `0F` | Intake air temp | 1 | `A-40` | °C |
| `10` | MAF air flow rate | 2 | `(256A+B)/100` | g/s |
| `11` | Throttle position | 1 | `A*100/255` | % |
| `14` | O2 sensor 1 voltage | 2 | `A/200` | V |
| `1F` | Run time since engine start | 2 | `256A+B` | s |
| `21` | Distance with MIL on | 2 | `256A+B` | km |
| `2F` | Fuel tank level | 1 | `A*100/255` | % |
| `42` | Control module voltage | 2 | `(256A+B)/1000` | V |
| `43` | Absolute load value | 2 | `(256A+B)*100/255` | % |
| `44` | Commanded equivalence ratio | 2 | `(256A+B)/32768` | lambda |
| `46` | Ambient air temp | 1 | `A-40` | °C |
| `5C` | Engine oil temp | 1 | `A-40` | °C |
| `5E` | Engine fuel rate | 2 | `(256A+B)/20` | L/h |

### Coverage notes

**Near-universal:** `04`, `05`, `0C`, `0D`, `0F`, `11` — required for emissions monitoring on essentially every OBD-II vehicle.

**Conditional:**
- `10` (MAF) — only on cars with a mass airflow sensor. Speed-density cars use `0B` (MAP) instead and have no MAF PID. Fuel economy must handle both paths.
- `08`/`09` (bank 2 trims) — only on V-configuration engines
- `14` — narrowband sensors only. Wideband cars report via `24`–`2B` as lambda, not 0–1V.
- `42`, `2F`, `5E`, `5C` — spotty, especially pre-2008

**No PID exists for oil level or oil consumption.** This is manufacturer-specific and requires a dedicated oil quantity sensor. Do not attempt to derive it from standard OBD-II.

---

## Mode 02 — freeze frame

Same PID formulas as Mode 01, but prefix `02` and append the frame number `00`.

- `020C00` → RPM at the moment the fault set
- `020500` → coolant temp at fault
- `020400` → engine load at fault

**Limits:** most ECUs store only one freeze frame, overwritten by the next fault. It captures a single instant, not a window. The parameter set is manufacturer-chosen.

---

## Other modes

| Mode | Purpose | Notes |
|---|---|---|
| `03` | Read stored DTCs | Returns pairs of bytes |
| `04` | Clear DTCs | Also erases readiness monitors — require confirmation |
| `06` | On-board test results | Includes pass/fail margins; few apps expose this |
| `07` | Pending DTCs | Failed once, not yet confirmed — early warning |
| `09 02` | Read VIN | Needed for cross-fleet baselines |
| `0A` | Permanent DTCs | Cannot be cleared by disconnecting battery |

### DTC decoding

Each code is 2 bytes. First 2 bits select the letter:

| Bits | Letter | System |
|---|---|---|
| `00` | P | Powertrain |
| `01` | C | Chassis |
| `10` | B | Body |
| `11` | U | Network |

Next 2 bits are the first digit, remaining 12 bits are three hex digits.

Example: `0171` → `P0171` (System Too Lean, Bank 1)

Bundle the generic SAE P0xxx list locally (public standard, ~2000 codes). Manufacturer-specific P1xxx codes are per-make — out of scope initially.

---

## Fuel economy calculation

**Preferred:** if PID `5E` is supported, use it directly.

**From MAF:**
```
L/h = (MAF_gps / 14.7 / 745) * 3600
km/L = speed_kph / (L/h)
```
Where 14.7 = stoichiometric AFR for gasoline, 745 g/L = gasoline density.

**From MAP (speed-density):** requires estimating airflow from MAP, RPM, IAT, and engine displacement. Less accurate. Flag results as estimated in the UI.

**Critical:** only ever compare fuel economy within matched condition buckets. See `DETECTION-RULES.md`.

---

## Throughput

- Every PID read is a request/response round trip
- ELM327 clone: ~5–15 queries/sec total
- OBDLink MX+ (STN): 100+ queries/sec

With six live gauges on a clone, each updates at roughly 1–2 Hz. Poll fast-changing PIDs (RPM, speed, throttle) more often than slow ones (coolant, fuel level). Make the per-PID poll interval configurable.
