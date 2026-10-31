using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AirSpawn
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("NuclearOption.exe")]
    public sealed class AirSpawnPlugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "com.mod.airspawn";
        public const string PluginName    = "Air Spawn";
        public const string PluginVersion = "1.2.0";

        public static AirSpawnPlugin? Instance { get; private set; }

        // Logger exposed statically so patches can log without holding a reference.
        public static ManualLogSource Log => Instance!.Logger;

        // "Settings" to avoid shadowing BaseUnityPlugin.Config (which is the raw ConfigFile).
        internal AirSpawnConfig Settings { get; private set; } = null!;

        private readonly Harmony _harmony = new Harmony(PluginGuid);

        private void Awake()
        {
            Instance = this;

            string presetsPath = Path.Combine(
                Paths.ConfigPath, "AirSpawnPresets.cfg");
            string scenariosPath = Path.Combine(
                Paths.ConfigPath, "AirSpawnScenarios.cfg");

            var presets   = new PerAircraftPresets(presetsPath, Logger);
            var scenarios = new ScenarioPresets(scenariosPath, Logger);
            Settings      = new AirSpawnConfig(Config, presets, scenarios, Logger);

            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            gameObject.AddComponent<RunwayQueue>();

            Logger.LogInfo($"[{PluginName}] v{PluginVersion} loaded.");
        }

        private void Update()
        {
            if (Settings.SaveScenarioKey.Value.IsDown()) HandleSaveScenario();
            if (Settings.CycleScenarioKey.Value.IsDown()) HandleCycleScenario();
        }

        private void HandleSaveScenario()
        {
            // CombatHUD.aircraft is the game's own reference to whichever aircraft
            // the local player currently pilots — null in the menu, on the ground
            // before spawning, or after ejecting/dying.
            Aircraft? aircraft = SceneSingleton<CombatHUD>.i?.aircraft;
            if (aircraft == null)
            {
                Logger.LogInfo(
                    "[AirSpawn] SaveScenarioKey pressed but you are not piloting an aircraft — nothing captured.");
                return;
            }

            GlobalPosition position = aircraft.transform.GlobalPosition();

            // Heading only — pitch/roll are discarded, same as the spawn patch
            // itself always spawns level. This is the inverse of the rotation the
            // spawn patch applies: Quaternion.Euler(0, heading, 0) * forward gives
            // (sin(heading), 0, cos(heading)) for a unit forward, so atan2(x, z)
            // recovers heading from it.
            Vector3 levelForward = Vector3.ProjectOnPlane(aircraft.transform.forward, Vector3.up);
            if (levelForward.sqrMagnitude < 0.001f) levelForward = Vector3.forward;
            levelForward.Normalize();
            float headingDeg = Mathf.Atan2(levelForward.x, levelForward.z) * Mathf.Rad2Deg;
            if (headingDeg < 0f) headingDeg += 360f;

            float speedMs = aircraft.rb.velocity.magnitude;

            string text = Settings.CaptureScenario(position, headingDeg, speedMs);
            Logger.LogInfo($"[AirSpawn] {text}");
            MessageUI.i?.GameMessage(text);
        }

        private void HandleCycleScenario()
        {
            string? text = Settings.CycleScenario();
            if (text == null)
            {
                Logger.LogInfo(
                    "[AirSpawn] CycleScenarioKey pressed but no scenario has been captured yet — " +
                    "press SaveScenarioKey while flying to capture one.");
                return;
            }

            Logger.LogInfo($"[AirSpawn] {text}");

            // MessageUI is the game's own local message feed — no networking, and it
            // expires the line itself. It only exists in gameplay scenes, so a null
            // here just means the key was pressed in a menu; the log line still records it.
            MessageUI.i?.GameMessage(text);
        }

        private void OnDestroy() => _harmony.UnpatchSelf();
    }
}
