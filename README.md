# Air Spawn — Nuclear Option mod

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
| **Spawn Airspeed** | `150` | Forward speed at spawn, in the selected unit. Default 150 m/s ≈ 291 kt ≈ 540 km/h ≈ 336 mph. |

### 2 — Per-Aircraft Presets

| Setting | Default | Description |
|---|---|---|
| **Enable Per-Aircraft Presets** | `false` | When on, each aircraft type can have its own altitude and airspeed. Falls back to the global values for aircraft without a saved preset. |
| **Selected Aircraft** | `(default)` | Dropdown populated after you enter a game. Select an aircraft to load its preset. |
| **Preset Altitude** | — | Altitude for the selected aircraft. Saved automatically. |
| **Preset Airspeed** | — | Airspeed for the selected aircraft in the active unit. Saved automatically. |

> **Note:** The dropdown and preset fields are hidden when Per-Aircraft Presets is disabled.  If they don't appear after toggling, close and re-open the config window (F1 → F1).

Presets are stored separately in `BepInEx/config/AirSpawnPresets.cfg`.  Each section is an aircraft `jsonKey`; altitudes are in metres and airspeeds are in m/s regardless of your display-unit setting.

---

## How it works

The normal spawn path calls a private `Hangar.SpawnAircraft` which routes through `Spawner.SpawnAircraft` with the hangar's transform and zero velocity.  This mod prefixes that private method and, for player spawns, calls `Spawner.SpawnAircraft` directly with:

- **Position** — hangar XZ + configured altitude
- **Rotation** — level, same heading as the hangar runway
- **Velocity** — forward × configured airspeed
- **`spawningHangar = null`** — tells every client to position from `NetworkstartPosition` / `NetworkstartingVelocity` rather than the hangar transform

Because `Aircraft.OnStartClient` already checks whether radar altitude exceeds the spawn offset (~2 m) and auto-retracts gear and sets throttle when it does, no extra work is needed for flight initialisation.

For carrier spawns the elevator animation plays normally; `SpawnAircraft` is intercepted once the animation finishes and the aircraft is placed in the air from that point.

---

## Building from source

1. Edit `GamePath.props` to point `GameDir` at your Nuclear Option installation.
2. `dotnet build -c Release`
3. The DLL is copied to `BepInEx/plugins/` automatically.

---

## Compatibility

Requires Nuclear Option **0.34 or newer** — 0.34 changed a game method this mod
calls, so v1.1.0 will not load correctly on 0.33.x.  Stay on v1.0.0 for 0.33.4.
Tested in-game on **0.34**.  Should work in multiplayer as host (server-side spawn path is the same).  Pure clients cannot trigger spawns and are unaffected.
