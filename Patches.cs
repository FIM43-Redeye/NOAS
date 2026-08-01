using System;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace AirSpawn
{
    // ──────────────────────────────────────────────────────────────────────────
    // Patch 1 — Hangar.SpawnAircraft (private)
    //
    // Replaces the normal ground spawn with an air spawn for player-controlled
    // aircraft whenever air spawn is enabled.  AI supply spawns (player == null)
    // are never touched.
    //
    // The critical parameter is spawningHangar = null.  Aircraft.OnStartClient
    // branches on NetworkspawningHangar:
    //   • non-null  → position from the hangar transform  (ground spawn)
    //   • null      → position from NetworkstartPosition / NetworkstartingVelocity
    //                 (the same path used for mission-editor-placed aircraft)
    //
    // Aircraft.OnStartClient also auto-handles gear and throttle:
    //   if (radarAlt > definition.spawnOffset.y + 1f)
    //   {   controlInputs.throttle = 0.6f; SetGear(false); }
    // At any reasonable altitude this condition is always true — no extra work.
    // ──────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(Hangar), "SpawnAircraft")]
    internal static class Patch_Hangar_SpawnAircraft
    {
        private static readonly FieldInfo SpawnedObjectField =
            AccessTools.Field(typeof(Hangar), "spawnedObject");

        private static bool Prefix(
            Hangar             __instance,
            Player             player,
            AircraftDefinition definition,
            Loadout            loadout,
            float              fuelLevel,
            LiveryKey          livery)
        {
            AirSpawnConfig cfg = AirSpawnPlugin.Instance!.Settings;

            if (!cfg.EnableAirSpawn.Value) return true;   // mod off — run original
            if (player == null)            return true;   // AI/supply spawn — never touch

            float altM  = cfg.GetAltitudeFor(definition.jsonKey);
            float spdMs = cfg.GetAirspeedMsFor(definition.jsonKey);

            // Level forward vector: strip pitch/roll from the hangar heading so
            // the aircraft starts in straight-and-level flight.
            Transform spawnTf      = __instance.GetSpawnTransform();
            Vector3   levelForward = Vector3.ProjectOnPlane(spawnTf.forward, Vector3.up);
            if (levelForward.sqrMagnitude < 0.001f) levelForward = Vector3.forward;
            levelForward.Normalize();

            Quaternion    levelRot  = Quaternion.LookRotation(levelForward, Vector3.up);
            GlobalPosition airPos   = spawnTf.GlobalPosition() + Vector3.up * altM;
            Vector3        startVel = levelForward * spdMs;

            Aircraft aircraft = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(
                player:         player,
                prefab:         definition.unitPrefab,
                loadout:        loadout,
                fuelLevel:      fuelLevel,
                livery:         livery,
                globalPosition: airPos,
                rotation:       levelRot,
                startingVel:    startVel,
                spawningHangar: null,                       // ← triggers air-spawn branch
                HQ:             __instance.attachedUnit.NetworkHQ,
                uniqueName:     null,
                skill:          1f,
                bravery:        0.5f);

            // 0.34 added the required Airbase parameter.  Hangar.SpawnAircraft passes
            // its own parentAirbase field here, so mirror that exactly; the method
            // falls back to HQ.GetNearestAirbase when it is null.
            if (loadout == null)
                aircraft.Networkloadout =
                    aircraft.weaponManager.SelectAIAircraftWeapons(__instance.parentAirbase);

            // Keep spawnedObject populated so the door-close coroutine has a
            // reference.  WaitForUnitToLeave sees the aircraft >50 m away
            // immediately and exits — the door animation is skipped naturally.
            SpawnedObjectField.SetValue(__instance, aircraft.gameObject);

            string abbr = SpeedConverter.Abbreviation(cfg.SpeedUnit.Value);
            float  disp = SpeedConverter.FromMs(spdMs, cfg.SpeedUnit.Value);
            AirSpawnPlugin.Log.LogInfo(
                $"[AirSpawn] {definition.unitName}  " +
                $"alt={altM:F0} m  spd={disp:F0} {abbr}");

            return false; // skip original Hangar.SpawnAircraft
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Patch 3 — Encyclopedia.AfterLoad (private static wrapper)
    //
    // The static AfterLoad(Encyclopedia instance) is called by the asset loader
    // after the Encyclopedia ScriptableObject finishes loading.  We piggyback
    // here to register all AircraftDefinitions so they appear in the config
    // dropdown.  This fires once per map load.
    // ──────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(Encyclopedia), "AfterLoad",
        new Type[] { typeof(Encyclopedia) })]
    internal static class Patch_Encyclopedia_AfterLoad
    {
        private static void Postfix(Encyclopedia instance)
        {
            if (instance?.aircraft == null) return;

            foreach (AircraftDefinition def in instance.aircraft)
            {
                if (def == null || string.IsNullOrEmpty(def.jsonKey)) continue;
                AircraftRegistry.Register(def.jsonKey, def.unitName ?? def.jsonKey);
            }

            AirSpawnPlugin.Log.LogDebug(
                $"[AirSpawn] Encyclopedia loaded — " +
                $"{instance.aircraft.Count} aircraft registered.");
        }
    }
}
