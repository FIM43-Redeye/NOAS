using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace AirSpawn
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("NuclearOption.exe")]
    public sealed class AirSpawnPlugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "com.mod.airspawn";
        public const string PluginName    = "Air Spawn";
        public const string PluginVersion = "1.1.0";

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

            var presets  = new PerAircraftPresets(presetsPath, Logger);
            Settings     = new AirSpawnConfig(Config, presets, Logger);

            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            Logger.LogInfo($"[{PluginName}] v{PluginVersion} loaded.");
        }

        private void OnDestroy() => _harmony.UnpatchSelf();
    }
}
