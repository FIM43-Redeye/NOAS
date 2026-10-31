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

            GlobalPosition airPos;
            Vector3        facing;
            float          spdMs;
            float          altM;         // for the log line only when not a scenario
            bool           headingActive;
            float          headingDeg;
            float          fwdOffsetM, rightOffsetM;

            if (cfg.TryGetActiveScenario(out GlobalPosition scenPos, out float scenHeadingDeg, out float scenSpdMs))
            {
                // A scenario is a saved place: use its absolute position and heading
                // directly. Altitude, heading override and position offset all exist
                // only to express "somewhere relative to whichever airbase I spawn
                // from," which a captured scenario has no need of — see TECHNICAL.md.
                airPos     = scenPos;
                facing     = Quaternion.Euler(0f, scenHeadingDeg, 0f) * Vector3.forward;
                spdMs      = scenSpdMs;
                altM       = scenPos.y;
                headingActive = true;
                headingDeg    = scenHeadingDeg;
                fwdOffsetM = rightOffsetM = 0f;
            }
            else
            {
                altM  = cfg.GetAltitudeFor(definition.jsonKey);
                spdMs = cfg.GetAirspeedMsFor(definition.jsonKey);

                // Level runway forward/right: strip pitch/roll from the runway heading.
                // Position Offset is always applied along THIS axis pair, never along
                // an overridden facing — it repositions relative to the airbase layout,
                // independent of which way the aircraft ends up pointed.
                // The hangar's own facing is only the fallback: hangars face their
                // taxiway, and a carrier's spawn point is its elevator, which faces
                // across the deck.
                Transform spawnTf      = __instance.GetSpawnTransform();
                Vector3   runwayDir    = TakeoffRunwayDirection(__instance, definition, spawnTf)
                                         ?? spawnTf.forward;
                Vector3   levelForward = Vector3.ProjectOnPlane(runwayDir, Vector3.up);
                if (levelForward.sqrMagnitude < 0.001f) levelForward = Vector3.forward;
                levelForward.Normalize();
                // Unity identity: right = Cross(up, forward) — left-handed, no negation.
                Vector3   levelRight   = Vector3.Cross(Vector3.up, levelForward);

                // Facing direction: the runway heading, unless a heading override is
                // active, in which case an absolute compass heading — 0 = north,
                // clockwise — replaces it. Compass and Unity's left-handed Y rotation
                // agree on this convention already: rotating world +Z (north) by
                // +heading around +Y sweeps toward +X (east).
                (headingActive, headingDeg) = cfg.GetHeadingOverride(definition.jsonKey);
                facing = headingActive
                    ? Quaternion.Euler(0f, headingDeg, 0f) * Vector3.forward
                    : levelForward;

                (fwdOffsetM, rightOffsetM) = cfg.GetPositionOffset(definition.jsonKey);

                airPos = spawnTf.GlobalPosition()
                    + levelForward * fwdOffsetM
                    + levelRight   * rightOffsetM
                    + Vector3.up   * altM;
            }

            Quaternion levelRot = Quaternion.LookRotation(facing, Vector3.up);
            Vector3    startVel = facing * spdMs;

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
            string scenPart = cfg.TryGetActiveScenario(out _, out _, out _) ? "  [scenario]" : "";
            string hdgPart = headingActive ? $"  hdg={headingDeg:F0}deg" : "";
            string offPart = (fwdOffsetM != 0f || rightOffsetM != 0f)
                ? $"  offset=({fwdOffsetM:F0},{rightOffsetM:F0})m" : "";
            AirSpawnPlugin.Log.LogInfo(
                $"[AirSpawn] {definition.unitName}{scenPart}  " +
                $"alt={altM:F0} m  spd={disp:F0} {abbr}{hdgPart}{offPart}");

            return false; // skip original Hangar.SpawnAircraft
        }

        /// <summary>
        /// Takeoff direction of the airbase's takeoff runway nearest the hangar,
        /// preferring runways long enough for the aircraft, or null if the
        /// airbase has none. Runway.GetDistance picks the end AI have been using
        /// for the last 30 s, else the nearer end, so this matches local traffic.
        /// On a carrier this is the flight deck.
        /// </summary>
        private static Vector3? TakeoffRunwayDirection(
            Hangar hangar, AircraftDefinition definition, Transform from)
        {
            Airbase airbase = hangar.parentAirbase;
            if (airbase == null || airbase.runways == null) return null;

            Airbase.Runway? best = null;
            bool  bestReverse = false, bestLongEnough = false;
            float bestDistance = float.MaxValue;
            float needed = definition.aircraftParameters.takeoffDistance;

            foreach (Airbase.Runway runway in airbase.runways)
            {
                if (runway == null || !runway.Takeoff) continue;

                bool longEnough = runway.Length >= needed;
                Airbase.Runway.RunwayDistanceResult distance = runway.GetDistance(from);
                bool better = longEnough != bestLongEnough
                    ? longEnough
                    : distance.Distance < bestDistance;
                if (best != null && !better) continue;

                best           = runway;
                bestReverse    = distance.Reverse;
                bestLongEnough = longEnough;
                bestDistance   = distance.Distance;
            }
            return best?.GetDirection(bestReverse);
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
