# Air Spawn

**Start in the air, not on the ramp. Skip the takeoff and get straight to flying.**

[![Latest release](https://img.shields.io/github/v/release/mosdef31/NOAS?style=for-the-badge&label=download&color=2ea043)](https://github.com/mosdef31/NOAS/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/mosdef31/NOAS/total?style=for-the-badge&color=blue)](https://github.com/mosdef31/NOAS/releases)
[![Game version](https://img.shields.io/badge/Nuclear%20Option-0.34%2B-orange?style=for-the-badge)](https://store.steampowered.com/app/2168680/Nuclear_Option/)
[![License](https://img.shields.io/badge/license-MIT-lightgrey?style=for-the-badge)](./LICENSE)

📥 **[Download the latest release](https://github.com/mosdef31/NOAS/releases/latest)** &nbsp;·&nbsp;
📝 **[What's new](https://github.com/mosdef31/NOAS/releases)** &nbsp;·&nbsp;
🔧 **[How it works](#how-it-works)** &nbsp;·&nbsp;
🐛 **[Report a bug](https://github.com/mosdef31/NOAS/issues)**

---

Spawns your aircraft in the air at a configurable altitude and airspeed after you pick your loadout, so you can jump straight into testing without repeating the takeoff every time.  AI units and mission-placed aircraft are never affected.

---

## Installation

1. Install **BepInEx 5** into your Nuclear Option directory if you haven't already.
2. Drop `AirSpawnMod.dll` into `BepInEx/plugins/`.
3. Launch the game.  The config file is created at `BepInEx/config/com.mod.airspawn.cfg` on first run.

### Optional — BepInEx.ConfigurationManager

Install [BepInEx.ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager) to edit all settings in-game with **F1**.

---

## Configuration

All settings live in `com.mod.airspawn.cfg`.  With ConfigurationManager installed you can change them at any time by pressing **F1**.

### 1 — Air Spawn

| Setting | Default | Description |
|---|---|---|
| **Enable** | `false` | Master switch. Toggle per session without restarting. |
| **Speed Unit** | `MetersPerSecond` | Unit for all airspeed inputs: `MetersPerSecond`, `KilometersPerHour`, `MilesPerHour`, `Knots`. Changing this auto-converts existing values. |
| **Spawn Altitude** | `1500` | Height in metres above the airbase. Gear retracts and throttle initialises automatically. |
| **Automatic Airspeed** | `false` | When on, each aircraft spawns at its own corner speed from the game's data (`AircraftParameters.cornerSpeed`, what the AI and fly-by-wire treat as best for manoeuvring: 90 m/s for the Cricket, 180 m/s for most fighters) instead of Spawn Airspeed. Aircraft with no takeoff speed, like helicopters, spawn at rest; their corner speed is at or above their top speed. Works for modded aircraft too, since it reads each aircraft's own data. Saved per-aircraft presets still win. |
| **Spawn Airspeed** | `150` | Forward speed at spawn, in the selected unit. Default 150 m/s ≈ 291 kt ≈ 540 km/h ≈ 336 mph. |
| **Enable Heading Override** | `false` | When on, spawn facing **Spawn Heading** instead of the runway's own heading. |
| **Spawn Heading** | `0` | Absolute compass heading in degrees, 0 = north, clockwise. Only read when Enable Heading Override is on. |
| **Forward Offset** | `0` | Position offset in metres along the runway's own heading, applied before altitude — independent of any heading override. Positive is further down the runway. |
| **Right Offset** | `0` | Position offset in metres to the right of the runway heading. |
| **Launch Acceleration** | `4` | You spawn held in place with your gear down (it starts coming up straight away), then get accelerated straight ahead at this many g (like a catapult shot) up to Spawn Airspeed, and released. Starting from rest means the takeoff music, flaps and slats, and modded parts behave as they would on a normal takeoff. |

### 2 — Per-Aircraft Presets

| Setting | Default | Description |
|---|---|---|
| **Enable Per-Aircraft Presets** | `false` | When on, each aircraft type can have its own altitude and airspeed. Falls back to the global values for aircraft without a saved preset. |
| **Selected Aircraft** | `(default)` | Dropdown populated after you enter a game. Select an aircraft to load its preset. |
| **Preset Altitude** | — | Altitude for the selected aircraft. Saved automatically. |
| **Preset Airspeed** | — | Airspeed for the selected aircraft in the active unit. Saved automatically. |

> **Note:** The dropdown and preset fields are hidden when Per-Aircraft Presets is disabled.  If they don't appear after toggling, close and re-open the config window (F1 → F1).

Presets are stored separately in `BepInEx/config/AirSpawnPresets.cfg`.  Each section is an aircraft `jsonKey`; altitudes are in metres and airspeeds are in m/s regardless of your display-unit setting. Presets cover altitude and airspeed only — not heading or position, which are a Section 3 concern.

### 3 — Scenario Presets

Named, exact spawn points — captured live from your own aircraft with a keybind, then picked from a menu, instead of dialling in numbers by hand. Fly to where you want, press **`LeftAlt + K`**, and that exact position, heading and airspeed are saved and selected. Fly somewhere else and do it again to build up a list, then pick between them from **Selected Scenario** or step through them with **`LeftAlt + S`**.

| Setting | Default | Description |
|---|---|---|
| **Enable Scenarios** | `false` | When on, the selected scenario's position, heading and airspeed override *everything above*, including per-aircraft presets — a scenario applies to every aircraft type until you switch or clear it. |
| **Selected Scenario** | `(none)` | Dropdown of captured scenarios. Choose one to spawn there next. |
| **Scenario Position** | *(read-only)* | The captured world position, shown for reference. Re-capture with SaveScenarioKey to change it — there's no field to type coordinates into. |
| **Scenario Heading** | `0` | Absolute compass heading in degrees, 0 = north, clockwise. Captured automatically; nudge it by hand afterwards if you want. |
| **Scenario Airspeed** | `150` | Airspeed for the selected scenario, in the active unit. Captured automatically; editable afterwards. |
| **SaveScenarioKey** | `LeftAlt + K` | While piloting an aircraft, captures its current position, heading and airspeed as a new scenario ("Scenario 1", "Scenario 2", ...), selects it, and shows a message. Does nothing if you're not in an aircraft. |
| **CycleScenarioKey** | `LeftAlt + S` | Cycles through every captured scenario, then to no active scenario, then back to the first. Shows an in-game message. Does nothing if no scenario has been captured yet. |

Scenarios are stored separately in `BepInEx/config/AirSpawnScenarios.cfg`, one section per scenario name, as an absolute world position rather than an offset — a scenario is a specific remembered place, not a rule that has to work at every airbase the way the global and per-aircraft settings do. See `TECHNICAL.md` if you're curious why those two use different coordinate systems.

### 4 — Runway Spawn

Skip the taxi instead of the takeoff: spawn lined up at the start of a runway, at rest, and take off yourself (takeoff music included).

| Setting | Default | Description |
|---|---|---|
| **Enable Runway Spawn** | `false` | When on, player aircraft spawn at the start of the nearest free takeoff runway long enough for them, pointed down it. Land airbases only; carriers, and aircraft that need no takeoff run (helicopters), spawn as normal. If no runway is free, you wait in line for one. |

A runway counts as free when nobody is landing on it or queued to take off from it (or from a runway crossing it), and no aircraft is sitting on it.

If none is free when you press Fly, your spawn is accepted as a delayed spawn (the same way carriers wait for their elevator) and a **Waiting for a runway** window shows your place in line. While anyone is waiting, AI aircraft holding short stay there; AI already entering the runway or on their takeoff roll carry on and clear it, and so do AI landings. Players are served first come, first served, on whichever suitable runway frees up first. The window's button lets you give up and spawn in the air (if **Enable** in section 1 is on) or in a hangar instead. The window is drawn by the host, so in multiplayer only the host sees it for their own spawn; other players simply wait for their aircraft to appear.

Once you spawn, you're put at the head of the runway's takeoff queue, so AI aircraft hold short until you're airborne or have left the runway. AI *landings* don't consult that queue (in the base game either), so don't park there for long. Another player on final approach isn't tracked by the game's runway traffic, so it won't stop a runway spawn either.

---

## How it works

The normal spawn path calls a private `Hangar.SpawnAircraft` which routes through `Spawner.SpawnAircraft` with the hangar's transform and zero velocity.  This mod prefixes that private method and, for player spawns, calls `Spawner.SpawnAircraft` directly with:

- **Position** — hangar XZ, offset along the runway's own heading by Forward/Right Offset, then raised by the configured altitude
- **Rotation** — the runway heading, unless a heading override (global or scenario) is active, in which case an absolute compass heading replaces it. The runway is the airbase's takeoff runway nearest the hangar (preferring ones long enough for the aircraft), in the direction AI have been using; on a carrier that's the flight deck. Only an airbase with no takeoff runway falls back to the hangar's own facing, since hangars face their taxiway and a carrier's spawn point is its elevator.
- **Velocity** — the spawn's actual facing (post-override) × configured airspeed
- **`spawningHangar = null`** — tells every client to position from `NetworkstartPosition` / `NetworkstartingVelocity` rather than the hangar transform

Position Offset is always measured along the runway's own axes, never along an overridden heading — it repositions relative to the airbase layout independent of which way the aircraft ends up pointed. This whole path is skipped when a scenario is active: a scenario supplies an absolute position and heading captured directly from your aircraft, with no airbase or runway involved at all. See `TECHNICAL.md` for the coordinate math and why scenarios use a different coordinate system from everything else.

Because `Aircraft.OnStartClient` already checks whether radar altitude exceeds the spawn offset (~2 m) and auto-retracts gear and sets throttle when it does, the engines and throttle need no extra work.

Spawning straight at speed does skip what happens between rest and flight, though. The game plays the takeoff music from `Aircraft.CheckRadarAlt` only when the aircraft first leaves the ground *with its gear down*, and speed-scheduled flaps and slats (`HighLiftDevice`) only animate when their target changes, so an aircraft spawned above their retract speed keeps whatever pose its model was built with. So on the local player's own machine, a postfix on `Aircraft.OnStartClient` turns the spawn into a launch: it puts the gear back down the way a ground spawn initialises it, holds the aircraft's attitude and height while accelerating it from rest along the spawn heading at **Launch Acceleration**, and releases it at the spawn velocity. The gear starts coming up as soon as the game has registered the aircraft as airborne (that check is what plays the music), so it folds during the launch. Everything that watches speed and gear sees a normal takeoff. A multiplayer client without the mod gets the plain air spawn at full speed. While **Enable** is on, a player aircraft placed in the air by a mission (no hangar, already airborne) gets the same launch, since the client can't tell the two apart.

For carrier spawns the elevator animation plays normally; `SpawnAircraft` is intercepted once the animation finishes and the aircraft is placed in the air from that point.

---

## AI use

I use an AI agent to help with coding, refactoring, asset modification, and authoring
long bodies of text and lore.

It raises the quality ceiling beyond what my own skills currently guarantee, while I
learn and develop them. Every decision, every number, and everything that ships is mine.

## Building from source

1. Edit `GamePath.props` to point `GameDir` at your Nuclear Option installation.
2. `dotnet build -c Release`
3. The DLL is copied to `BepInEx/plugins/` automatically.

---

## Compatibility

Requires Nuclear Option **0.34 or newer** — 0.34 changed a game method this mod
calls, so v1.1.0 will not load correctly on 0.33.x.  Stay on v1.0.0 for 0.33.4.
Tested in-game on **0.34**.  Should work in multiplayer as host (server-side spawn path is the same).  Pure clients cannot trigger spawns and are unaffected.
