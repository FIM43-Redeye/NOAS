using System;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace AirSpawn
{
    /// <summary>
    /// Stores named scenario presets — an absolute world position, compass
    /// heading and airspeed, captured live from the player's aircraft and
    /// swappable by name — in a separate file (BepInEx/config/AirSpawnScenarios.cfg).
    ///
    /// Unlike the global or per-aircraft settings, which apply at any airbase and
    /// so can only be expressed relative to wherever the aircraft happens to
    /// spawn, a scenario is inherently tied to one specific place: it is captured
    /// FROM a specific position, so it is stored as one — <see cref="GlobalPosition"/>,
    /// not an offset.
    ///
    /// Unlike <see cref="PerAircraftPresets"/>, which is keyed by aircraft
    /// jsonKey and populated from the Encyclopedia, scenario names are assigned
    /// automatically at capture time ("Scenario 1", "Scenario 2", ...). The
    /// "_Index" section's "Names" key is the only thing that remembers which
    /// scenarios exist and their cycle/dropdown order — everything else is
    /// discovered by binding a section named after the scenario.
    /// </summary>
    internal sealed class ScenarioPresets
    {
        private readonly ConfigFile _file;
        private readonly ManualLogSource _log;
        private readonly ConfigEntry<string> _names;

        /// <summary>Raised whenever a scenario is captured, so the config
        /// dropdown can refresh — mirrors <see cref="AircraftRegistry.Updated"/>.</summary>
        internal event Action? Updated;

        internal ScenarioPresets(string filePath, ManualLogSource log)
        {
            _log = log;
            _file = new ConfigFile(filePath, saveOnInit: true);

            _names = _file.Bind(
                "_Index", "Names", "",
                "Semicolon-separated list of scenario names, in cycle/dropdown order. " +
                "Managed automatically when you capture a scenario with SaveScenarioKey " +
                "— edit by hand only to reorder or delete one.");
        }

        /// <summary>Scenario names in cycle/dropdown order.</summary>
        internal string[] Names =>
            string.IsNullOrEmpty(_names.Value)
                ? Array.Empty<string>()
                : _names.Value
                    .Split(';')
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToArray();

        internal bool Contains(string name) =>
            !string.IsNullOrEmpty(name) &&
            Names.Contains(name, StringComparer.OrdinalIgnoreCase);

        /// <summary>The next unused "Scenario N" name, for auto-naming a capture.</summary>
        internal string NextAutoName()
        {
            int n = 1;
            while (Contains($"Scenario {n}")) n++;
            return $"Scenario {n}";
        }

        internal (GlobalPosition position, float headingDeg, float airspeedMs) Get(string name)
        {
            var px = _file.Bind(name, "PositionX", 0f,
                new ConfigDescription("Captured world X position, metres."));
            var py = _file.Bind(name, "PositionY", 0f,
                new ConfigDescription("Captured world Y position (altitude), metres."));
            var pz = _file.Bind(name, "PositionZ", 0f,
                new ConfigDescription("Captured world Z position, metres."));
            var hdg = _file.Bind(name, "HeadingDeg", 0f,
                new ConfigDescription(
                    "Absolute compass heading in degrees, 0 = north, clockwise, " +
                    "captured from the aircraft's facing direction at save time."));
            var spd = _file.Bind(name, "AirspeedMs", 0f,
                new ConfigDescription("Captured airspeed in m/s."));

            return (new GlobalPosition(px.Value, py.Value, pz.Value), hdg.Value, spd.Value);
        }

        /// <summary>
        /// Persists a scenario and registers its name if new. Used both by the
        /// capture hotkey (new name, all three values fresh) and by hand-editing
        /// heading or airspeed for an existing scenario (same name, position
        /// carried over from <see cref="Get"/> unchanged).
        /// </summary>
        internal void Set(string name, GlobalPosition position, float headingDeg, float airspeedMs)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            EnsureRegistered(name);

            _file.Bind(name, "PositionX", 0f, "").Value = position.x;
            _file.Bind(name, "PositionY", 0f, "").Value = position.y;
            _file.Bind(name, "PositionZ", 0f, "").Value = position.z;
            _file.Bind(name, "HeadingDeg", 0f, "").Value = headingDeg;
            _file.Bind(name, "AirspeedMs", 0f, "").Value = airspeedMs;
            _file.Save();

            _log.LogDebug(
                $"[AirSpawn] Saved scenario [{name}] pos={position} " +
                $"hdg={headingDeg:F0} spd={airspeedMs:F0} m/s");
        }

        private void EnsureRegistered(string name)
        {
            if (Contains(name)) return;

            var list = Names.ToList();
            list.Add(name);
            _names.Value = string.Join(";", list);

            _log.LogInfo($"[AirSpawn] New scenario registered: {name}");
            Updated?.Invoke();
        }
    }
}
