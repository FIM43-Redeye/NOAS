using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace AirSpawn
{
    /// <summary>
    /// Owns every <see cref="ConfigEntry{T}"/> and wires the runtime behaviours:
    ///   • speed-unit conversion when the unit selection changes
    ///   • per-aircraft preset load / auto-save
    ///   • named scenario preset load / auto-save / dropdown / hotkey cycling
    ///   • aircraft dropdown refresh when <see cref="AircraftRegistry"/> updates
    ///
    /// A scenario, if enabled and selected, bypasses the global and per-aircraft
    /// values entirely — see <see cref="TryGetActiveScenario"/> and
    /// <c>Patch_Hangar_SpawnAircraft</c>. It is captured live (position, heading,
    /// airspeed) rather than authored as an offset, because unlike the global or
    /// per-aircraft settings — which must generalise across every airbase — a
    /// scenario is inherently tied to one specific place.
    /// </summary>
    internal sealed class AirSpawnConfig
    {
        // ── Constants ────────────────────────────────────────────────────────
        private const string Sec1 = "1 - Air Spawn";
        private const string Sec2 = "2 - Per-Aircraft Presets";
        private const string Sec3 = "3 - Scenario Presets";
        private const string Sec4 = "4 - Runway Spawn";
        private const string NoAircraft = "(default)";
        private const string NoScenario = "(none)";
        private const string NoPositionDisplay = "(none captured)";

        // Airspeed range covers ≈ Mach 3 in every unit:
        //   m/s: 3–1030   km/h: 10–3710   mph: 6–2300   kt: 5–2000
        // We use a single wide range so AcceptableValueRange never rejects
        // a converted value after a unit change.
        private const float SpeedMin = 3f;
        private const float SpeedMax = 3710f;

        // ── Public ConfigEntries (read by patches) ────────────────────────
        internal ConfigEntry<bool>      EnableAirSpawn    { get; }
        internal ConfigEntry<float>     SpawnAltitude     { get; }   // metres
        internal ConfigEntry<float>     SpawnAirspeed     { get; }   // display unit
        internal ConfigEntry<SpeedUnit> SpeedUnit         { get; }
        internal ConfigEntry<bool>      EnableHeadingOverride { get; }
        internal ConfigEntry<float>     SpawnHeadingDeg   { get; }   // degrees, 0 = north
        internal ConfigEntry<float>     ForwardOffsetM    { get; }   // along runway heading
        internal ConfigEntry<float>     RightOffsetM      { get; }   // across runway heading

        internal ConfigEntry<bool>      EnablePerAircraft { get; }
        internal ConfigEntry<string>    SelectedAircraft  { get; }
        internal ConfigEntry<float>     PresetAltitude    { get; }   // metres
        internal ConfigEntry<float>     PresetAirspeed    { get; }   // display unit

        internal ConfigEntry<bool>      EnableScenarios         { get; }
        internal ConfigEntry<string>    SelectedScenario        { get; }
        internal ConfigEntry<string>    ScenarioPositionDisplay { get; }   // read-only
        internal ConfigEntry<float>     ScenarioHeadingDeg      { get; }   // degrees, 0 = north
        internal ConfigEntry<float>     ScenarioAirspeed        { get; }   // display unit
        internal ConfigEntry<KeyboardShortcut> SaveScenarioKey  { get; }
        internal ConfigEntry<KeyboardShortcut> CycleScenarioKey { get; }

        internal ConfigEntry<bool>      EnableRunwaySpawn { get; }

        // ── Private state ────────────────────────────────────────────────────
        private readonly PerAircraftPresets _presets;
        private readonly ScenarioPresets    _scenarios;
        private readonly ManualLogSource    _log;

        private SpeedUnit _prevUnit;
        private bool      _loadingPreset;
        private bool      _loadingScenario;

        // ConfigurationManagerAttributes instances held by reference so their
        // Browsable flag can be flipped at runtime.
        private readonly ConfigurationManagerAttributes _selAttr =
            new ConfigurationManagerAttributes { Order = 30 };
        private readonly ConfigurationManagerAttributes _preAltAttr =
            new ConfigurationManagerAttributes { Order = 20 };
        private readonly ConfigurationManagerAttributes _preSpdAttr =
            new ConfigurationManagerAttributes { Order = 10 };

        private readonly ConfigurationManagerAttributes _scnSelAttr =
            new ConfigurationManagerAttributes { Order = 40 };
        private readonly ConfigurationManagerAttributes _scnPosAttr =
            new ConfigurationManagerAttributes { Order = 30, ReadOnly = true };
        private readonly ConfigurationManagerAttributes _scnHdgAttr =
            new ConfigurationManagerAttributes { Order = 20 };
        private readonly ConfigurationManagerAttributes _scnSpdAttr =
            new ConfigurationManagerAttributes { Order = 10 };

        // AcceptableValueList for the aircraft dropdown and its backing field.
        private readonly AcceptableValueList<string> _aircraftList =
            new AcceptableValueList<string>(NoAircraft);

        // Same trick for the scenario dropdown.
        private readonly AcceptableValueList<string> _scenarioList =
            new AcceptableValueList<string>(NoScenario);

        // We update a list's backing array at runtime via reflection so that
        // ConfigurationManager's combo-box renders the full aircraft/scenario list.
        private static readonly FieldInfo? AvlBackingField =
            typeof(AcceptableValueList<string>)
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType == typeof(string[]));

        // ── Constructor ──────────────────────────────────────────────────────
        internal AirSpawnConfig(
            ConfigFile          cfg,
            PerAircraftPresets  presets,
            ScenarioPresets     scenarios,
            ManualLogSource     log)
        {
            _presets   = presets;
            _scenarios = scenarios;
            _log       = log;

            // ── Section 1: Air Spawn ─────────────────────────────────────────
            EnableAirSpawn = cfg.Bind(
                Sec1, "Enable", false,
                new ConfigDescription(
                    "Master switch — when on, player aircraft spawn in the air.\n" +
                    "AI units and mission-placed aircraft are never affected.",
                    null,
                    new ConfigurationManagerAttributes { Order = 50 }));

            SpeedUnit = cfg.Bind(
                Sec1, "Speed Unit", AirSpawn.SpeedUnit.MetersPerSecond,
                new ConfigDescription(
                    "Unit used for all airspeed values in this config.\n" +
                    "Changing this automatically converts the stored speed values.\n" +
                    "Reference: 1 m/s = 1.944 kt = 3.6 km/h = 2.237 mph",
                    null,
                    new ConfigurationManagerAttributes { Order = 45 }));

            SpawnAltitude = cfg.Bind(
                Sec1, "Spawn Altitude", 1500f,
                new ConfigDescription(
                    "Height in metres above the airbase at which to spawn.\n" +
                    "Gear retracts and throttle initialises automatically at any altitude above ~2 m.",
                    new AcceptableValueRange<float>(100f, 15000f),
                    new ConfigurationManagerAttributes { Order = 40 }));

            SpawnAirspeed = cfg.Bind(
                Sec1, "Spawn Airspeed", 150f,
                new ConfigDescription(
                    BuildSpeedDesc(150f, AirSpawn.SpeedUnit.MetersPerSecond),
                    new AcceptableValueRange<float>(SpeedMin, SpeedMax),
                    new ConfigurationManagerAttributes { Order = 35 }));

            EnableHeadingOverride = cfg.Bind(
                Sec1, "Enable Heading Override", false,
                new ConfigDescription(
                    "When on, spawn facing Spawn Heading instead of the runway's own heading.\n" +
                    "This section applies at any airbase, so heading is a compass bearing rather " +
                    "than a saved place — for a specific remembered spot, use a Scenario instead.",
                    null,
                    new ConfigurationManagerAttributes { Order = 32 }));

            SpawnHeadingDeg = cfg.Bind(
                Sec1, "Spawn Heading", 0f,
                new ConfigDescription(
                    "Absolute compass heading in degrees, 0 = north, clockwise. " +
                    "Only used when Enable Heading Override is on.",
                    new AcceptableValueRange<float>(0f, 360f),
                    new ConfigurationManagerAttributes { Order = 31 }));

            ForwardOffsetM = cfg.Bind(
                Sec1, "Forward Offset", 0f,
                new ConfigDescription(
                    "Position offset in metres along the runway's own heading, " +
                    "applied before altitude and independent of any heading override. " +
                    "Positive is further down the runway, negative is behind the hangar. " +
                    "Runway-relative because this section has to work at any airbase.",
                    new AcceptableValueRange<float>(-5000f, 5000f),
                    new ConfigurationManagerAttributes { Order = 30 }));

            RightOffsetM = cfg.Bind(
                Sec1, "Right Offset", 0f,
                new ConfigDescription(
                    "Position offset in metres to the right of the runway heading.",
                    new AcceptableValueRange<float>(-5000f, 5000f),
                    new ConfigurationManagerAttributes { Order = 29 }));

            // ── Section 2: Per-Aircraft Presets ──────────────────────────────
            EnablePerAircraft = cfg.Bind(
                Sec2, "Enable Per-Aircraft Presets", false,
                new ConfigDescription(
                    "When on, each aircraft type can have its own spawn altitude and airspeed.\n" +
                    "Falls back to the global values for any aircraft without a saved preset.\n" +
                    "Close and re-open the config window to show or hide the controls below.",
                    null,
                    new ConfigurationManagerAttributes { Order = 50 }));

            SelectedAircraft = cfg.Bind(
                Sec2, "Selected Aircraft", NoAircraft,
                new ConfigDescription(
                    "Choose an aircraft to view or edit its preset.\n" +
                    "Aircraft appear here after entering a game (Encyclopedia loads on map load).\n" +
                    "Select an aircraft, adjust the values below, and they save automatically.",
                    _aircraftList,
                    _selAttr));

            PresetAltitude = cfg.Bind(
                Sec2, "Preset Altitude", 1500f,
                new ConfigDescription(
                    "Spawn altitude in metres for the selected aircraft. Saved automatically.",
                    new AcceptableValueRange<float>(100f, 15000f),
                    _preAltAttr));

            PresetAirspeed = cfg.Bind(
                Sec2, "Preset Airspeed", 150f,
                new ConfigDescription(
                    "Spawn airspeed (in the Speed Unit chosen above) for the selected aircraft.\n" +
                    "Saved automatically.",
                    new AcceptableValueRange<float>(SpeedMin, SpeedMax),
                    _preSpdAttr));

            // ── Section 3: Scenario Presets ──────────────────────────────────
            EnableScenarios = cfg.Bind(
                Sec3, "Enable Scenarios", false,
                new ConfigDescription(
                    "When on, the selected scenario's position, heading and airspeed override " +
                    "everything above, including per-aircraft presets — a scenario applies to " +
                    "every aircraft type until you switch or clear it.",
                    null,
                    new ConfigurationManagerAttributes { Order = 60 }));

            SelectedScenario = cfg.Bind(
                Sec3, "Selected Scenario", NoScenario,
                new ConfigDescription(
                    "Choose a captured scenario to spawn at. Press SaveScenarioKey while flying " +
                    "to capture a new one from your current aircraft.",
                    _scenarioList,
                    _scnSelAttr));

            ScenarioPositionDisplay = cfg.Bind(
                Sec3, "Scenario Position", NoPositionDisplay,
                new ConfigDescription(
                    "World position captured for the selected scenario. Read-only — " +
                    "re-capture with SaveScenarioKey to change it.",
                    null,
                    _scnPosAttr));

            ScenarioHeadingDeg = cfg.Bind(
                Sec3, "Scenario Heading", 0f,
                new ConfigDescription(
                    "Absolute compass heading in degrees, 0 = north, clockwise, for the " +
                    "selected scenario. Captured automatically; editable afterwards. Saved automatically.",
                    new AcceptableValueRange<float>(0f, 360f),
                    _scnHdgAttr));

            ScenarioAirspeed = cfg.Bind(
                Sec3, "Scenario Airspeed", 150f,
                new ConfigDescription(
                    "Spawn airspeed (in the Speed Unit chosen above) for the selected scenario.\n" +
                    "Captured automatically; editable afterwards. Saved automatically.",
                    new AcceptableValueRange<float>(SpeedMin, SpeedMax),
                    _scnSpdAttr));

            SaveScenarioKey = cfg.Bind(
                Sec3, "SaveScenarioKey",
                new KeyboardShortcut(KeyCode.K, KeyCode.LeftAlt),
                new ConfigDescription(
                    "While piloting an aircraft, captures its current position, heading and " +
                    "airspeed as a new scenario (named \"Scenario 1\", \"Scenario 2\", ...), " +
                    "selects it, and shows a message. Does nothing if you are not in an aircraft.",
                    null,
                    new ConfigurationManagerAttributes { Order = 6 }));

            CycleScenarioKey = cfg.Bind(
                Sec3, "CycleScenarioKey",
                new KeyboardShortcut(KeyCode.S, KeyCode.LeftAlt),
                new ConfigDescription(
                    "Cycles the selected scenario in-game and shows a message: through every " +
                    "captured scenario in order, then to no active scenario, then back to the " +
                    "first. Does nothing if no scenario has been captured yet.",
                    null,
                    new ConfigurationManagerAttributes { Order = 5 }));

            // ── Section 4: Runway Spawn ──────────────────────────────────────
            EnableRunwaySpawn = cfg.Bind(
                Sec4, "Enable Runway Spawn", false,
                new ConfigDescription(
                    "When on, player aircraft spawn at the start of a free takeoff runway, " +
                    "lined up and ready to go, instead of in the hangar. Land airbases only; " +
                    "carriers, and aircraft that need no takeoff run (helicopters), spawn as normal.\n" +
                    "If no runway is free, you wait in line for one, and AI aircraft holding " +
                    "short wait with you. The runway is then held for you until you take off " +
                    "or leave it.",
                    null,
                    new ConfigurationManagerAttributes { Order = 50 }));

            // ── Wire events ──────────────────────────────────────────────────
            _prevUnit = SpeedUnit.Value;
            SetPresetBrowsable(EnablePerAircraft.Value);
            SetScenarioBrowsable(EnableScenarios.Value);

            SpeedUnit.SettingChanged         += OnSpeedUnitChanged;
            EnablePerAircraft.SettingChanged += OnEnablePerAircraftChanged;
            SelectedAircraft.SettingChanged  += OnSelectedAircraftChanged;
            PresetAltitude.SettingChanged    += OnPresetValueChanged;
            PresetAirspeed.SettingChanged    += OnPresetValueChanged;

            EnableScenarios.SettingChanged  += OnEnableScenariosChanged;
            SelectedScenario.SettingChanged += OnSelectedScenarioChanged;
            ScenarioHeadingDeg.SettingChanged += OnScenarioValueChanged;
            ScenarioAirspeed.SettingChanged   += OnScenarioValueChanged;

            AircraftRegistry.Updated += RefreshAircraftDropdown;
            _scenarios.Updated       += RefreshScenarioDropdown;
            RefreshScenarioDropdown();
        }

        // ── Public helpers used by patches ────────────────────────────────────

        /// <summary>Spawn altitude in metres for <paramref name="jsonKey"/>.</summary>
        internal float GetAltitudeFor(string jsonKey)
        {
            if (!EnablePerAircraft.Value || !AircraftRegistry.Contains(jsonKey))
                return SpawnAltitude.Value;

            var (altM, _) = _presets.Get(jsonKey, SpawnAltitude.Value, GetGlobalAirspeedMs());
            return altM;
        }

        /// <summary>Spawn airspeed in m/s for <paramref name="jsonKey"/>.</summary>
        internal float GetAirspeedMsFor(string jsonKey)
        {
            if (!EnablePerAircraft.Value || !AircraftRegistry.Contains(jsonKey))
                return GetGlobalAirspeedMs();

            var (_, spdMs) = _presets.Get(jsonKey, SpawnAltitude.Value, GetGlobalAirspeedMs());
            return spdMs;
        }

        /// <summary>
        /// Whether a heading override is active for this spawn, and the absolute
        /// compass heading in degrees if so. Only the global setting — a scenario,
        /// if active, is handled entirely by <see cref="TryGetActiveScenario"/>
        /// instead, upstream of this call; per-aircraft presets never carry heading.
        /// </summary>
        internal (bool active, float headingDeg) GetHeadingOverride(string jsonKey) =>
            EnableHeadingOverride.Value ? (true, SpawnHeadingDeg.Value) : (false, 0f);

        /// <summary>
        /// Position offset in metres, (forward along the runway heading, right
        /// across it), for <paramref name="jsonKey"/>. Defaults to (0, 0) — a
        /// no-op — so this is always safe to apply.
        /// </summary>
        internal (float forwardM, float rightM) GetPositionOffset(string jsonKey) =>
            (ForwardOffsetM.Value, RightOffsetM.Value);

        /// <summary>
        /// If a scenario is enabled and the selection names one that has actually
        /// been captured, returns its absolute world position, compass heading and
        /// airspeed and takes over the whole spawn — bypassing altitude, heading
        /// override and position offset entirely, since those exist only to
        /// express "somewhere relative to whichever airbase I spawn from," which a
        /// captured scenario has no need of.
        /// </summary>
        internal bool TryGetActiveScenario(out GlobalPosition position, out float headingDeg, out float airspeedMs)
        {
            if (EnableScenarios.Value && _scenarios.Contains(SelectedScenario.Value))
            {
                (position, headingDeg, airspeedMs) = _scenarios.Get(SelectedScenario.Value);
                return true;
            }

            position = default;
            headingDeg = 0f;
            airspeedMs = 0f;
            return false;
        }

        /// <summary>
        /// Captures <paramref name="position"/>/<paramref name="headingDeg"/>/
        /// <paramref name="airspeedMs"/> as a new, auto-named scenario, selects it,
        /// and returns a human-readable line for the caller to show as a message.
        /// </summary>
        internal string CaptureScenario(GlobalPosition position, float headingDeg, float airspeedMs)
        {
            string name = _scenarios.NextAutoName();
            _scenarios.Set(name, position, headingDeg, airspeedMs);
            SelectedScenario.Value = name; // triggers OnSelectedScenarioChanged → loads the display fields
            return $"Air Spawn scenario captured: {name}  ({position}  hdg={headingDeg:F0}°)";
        }

        /// <summary>
        /// Advances <see cref="SelectedScenario"/> to the next name in cycle
        /// order (then to "no active scenario", then back to the first), and
        /// returns a human-readable line describing the result — or null if no
        /// scenario has been captured yet, so the caller can skip showing a
        /// message.
        /// </summary>
        internal string? CycleScenario()
        {
            var names = _scenarios.Names;
            if (names.Length == 0) return null;

            int current = Array.IndexOf(names, SelectedScenario.Value);
            // current == -1 covers both "no active scenario" and "a name that
            // no longer exists" — either way the next state is the first entry.
            string next = current + 1 < names.Length ? names[current + 1] : NoScenario;

            SelectedScenario.Value = next;

            return next == NoScenario
                ? "Air Spawn scenario: none (global / per-aircraft values apply)"
                : $"Air Spawn scenario: {next}";
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private void OnSpeedUnitChanged(object sender, EventArgs e)
        {
            var newUnit = SpeedUnit.Value;
            if (newUnit == _prevUnit) return;

            // Convert the global airspeed entry value to the new unit
            float ms = SpeedConverter.ToMs(SpawnAirspeed.Value, _prevUnit);
            SpawnAirspeed.Value = Mathf.Round(SpeedConverter.FromMs(ms, newUnit));

            // Convert the displayed preset airspeed (the stored m/s value is unchanged)
            if (EnablePerAircraft.Value && SelectedAircraft.Value != NoAircraft)
            {
                float presetMs = SpeedConverter.ToMs(PresetAirspeed.Value, _prevUnit);
                _loadingPreset = true;
                PresetAirspeed.Value = Mathf.Round(SpeedConverter.FromMs(presetMs, newUnit));
                _loadingPreset = false;
                // No need to re-save: the preset file stores m/s and hasn't changed.
            }

            // Same conversion for the displayed scenario airspeed.
            if (SelectedScenario.Value != NoScenario)
            {
                float scenarioMs = SpeedConverter.ToMs(ScenarioAirspeed.Value, _prevUnit);
                _loadingScenario = true;
                ScenarioAirspeed.Value = Mathf.Round(SpeedConverter.FromMs(scenarioMs, newUnit));
                _loadingScenario = false;
                // No need to re-save: the scenario file stores m/s and hasn't changed.
            }

            _prevUnit = newUnit;

            _log.LogInfo(
                $"[AirSpawn] Speed unit → {SpeedConverter.Abbreviation(newUnit)}  " +
                $"(global airspeed = {SpawnAirspeed.Value:F0} {SpeedConverter.Abbreviation(newUnit)})");
        }

        private void OnEnablePerAircraftChanged(object sender, EventArgs e)
        {
            SetPresetBrowsable(EnablePerAircraft.Value);
        }

        private void OnEnableScenariosChanged(object sender, EventArgs e)
        {
            SetScenarioBrowsable(EnableScenarios.Value);
        }

        private void OnSelectedAircraftChanged(object sender, EventArgs e)
        {
            LoadPresetForSelected();
        }

        private void OnPresetValueChanged(object sender, EventArgs e)
        {
            if (_loadingPreset) return;
            SaveCurrentPreset();
        }

        private void OnSelectedScenarioChanged(object sender, EventArgs e)
        {
            LoadScenarioForSelected();
        }

        private void OnScenarioValueChanged(object sender, EventArgs e)
        {
            if (_loadingScenario) return;
            SaveCurrentScenario();
        }

        // ── Preset load / save ────────────────────────────────────────────────

        private void LoadPresetForSelected()
        {
            _loadingPreset = true;
            try
            {
                var key = SelectedAircraft.Value;
                if (key == NoAircraft || !AircraftRegistry.Contains(key))
                {
                    PresetAltitude.Value = SpawnAltitude.Value;
                    PresetAirspeed.Value = SpawnAirspeed.Value;
                    return;
                }

                var (altM, spdMs) = _presets.Get(
                    key,
                    SpawnAltitude.Value,
                    GetGlobalAirspeedMs());

                PresetAltitude.Value = altM;
                PresetAirspeed.Value = Mathf.Round(
                    SpeedConverter.FromMs(spdMs, SpeedUnit.Value));
            }
            finally
            {
                _loadingPreset = false;
            }
        }

        private void SaveCurrentPreset()
        {
            var key = SelectedAircraft.Value;
            if (key == NoAircraft) return;

            _presets.Set(
                key,
                PresetAltitude.Value,
                SpeedConverter.ToMs(PresetAirspeed.Value, SpeedUnit.Value));
        }

        // ── Scenario load / save ─────────────────────────────────────────────

        private void LoadScenarioForSelected()
        {
            _loadingScenario = true;
            try
            {
                var name = SelectedScenario.Value;
                if (name == NoScenario || !_scenarios.Contains(name))
                {
                    ScenarioPositionDisplay.Value = NoPositionDisplay;
                    ScenarioHeadingDeg.Value       = 0f;
                    ScenarioAirspeed.Value         = SpawnAirspeed.Value;
                    return;
                }

                var (pos, hdg, spdMs) = _scenarios.Get(name);

                ScenarioPositionDisplay.Value = pos.ToString();
                ScenarioHeadingDeg.Value      = hdg;
                ScenarioAirspeed.Value        = Mathf.Round(SpeedConverter.FromMs(spdMs, SpeedUnit.Value));
            }
            finally
            {
                _loadingScenario = false;
            }
        }

        private void SaveCurrentScenario()
        {
            var name = SelectedScenario.Value;
            if (name == NoScenario || !_scenarios.Contains(name)) return;

            // Heading/airspeed may have been hand-edited; position is
            // capture-only, so carry the stored value over unchanged.
            var (pos, _, _) = _scenarios.Get(name);

            _scenarios.Set(
                name,
                pos,
                ScenarioHeadingDeg.Value,
                SpeedConverter.ToMs(ScenarioAirspeed.Value, SpeedUnit.Value));
        }

        // ── Dropdown refresh ──────────────────────────────────────────────────

        private void RefreshAircraftDropdown()
        {
            if (AvlBackingField == null)
            {
                _log.LogWarning(
                    "[AirSpawn] Could not update aircraft dropdown: " +
                    "AcceptableValueList backing field not found. " +
                    "Type the aircraft jsonKey manually into Selected Aircraft.");
                return;
            }

            var values = new[] { NoAircraft }
                .Concat(AircraftRegistry.SortedKeys())
                .ToArray();

            AvlBackingField.SetValue(_aircraftList, values);

            // If the previously selected key is no longer in the list, reset.
            if (!values.Contains(SelectedAircraft.Value))
                SelectedAircraft.Value = NoAircraft;

            _log.LogDebug(
                $"[AirSpawn] Aircraft dropdown refreshed — {values.Length - 1} entries.");
        }

        private void RefreshScenarioDropdown()
        {
            if (AvlBackingField == null)
            {
                _log.LogWarning(
                    "[AirSpawn] Could not update scenario dropdown: " +
                    "AcceptableValueList backing field not found. " +
                    "Type the scenario name manually into Selected Scenario.");
                return;
            }

            var values = new[] { NoScenario }
                .Concat(_scenarios.Names)
                .ToArray();

            AvlBackingField.SetValue(_scenarioList, values);

            _log.LogDebug(
                $"[AirSpawn] Scenario dropdown refreshed — {values.Length - 1} entries.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private float GetGlobalAirspeedMs() =>
            SpeedConverter.ToMs(SpawnAirspeed.Value, SpeedUnit.Value);

        private void SetPresetBrowsable(bool visible)
        {
            _selAttr.Browsable    = visible;
            _preAltAttr.Browsable = visible;
            _preSpdAttr.Browsable = visible;
        }

        private void SetScenarioBrowsable(bool visible)
        {
            _scnSelAttr.Browsable = visible;
            _scnPosAttr.Browsable = visible;
            _scnHdgAttr.Browsable = visible;
            _scnSpdAttr.Browsable = visible;
        }

        private static string BuildSpeedDesc(float defaultMs, SpeedUnit unit)
        {
            float display = SpeedConverter.FromMs(defaultMs, unit);
            string abbr   = SpeedConverter.Abbreviation(unit);
            return
                $"Forward airspeed at spawn, in {abbr}.\n" +
                $"Default {display:F0} {abbr} ≈ 150 m/s ≈ 291 kt ≈ 540 km/h ≈ 336 mph.\n" +
                $"Change 'Speed Unit' above to enter the value in a different unit.";
        }
    }
}
