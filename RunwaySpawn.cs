using System.Collections;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace AirSpawn
{
    // ──────────────────────────────────────────────────────────────────────────
    // Runway spawn — called from Patch_Hangar_SpawnAircraft.
    //
    // Places a player's aircraft at the start of a free takeoff runway, pointed
    // down it, at rest. It uses the same spawningHangar = null path as an air
    // spawn; because the aircraft is on the ground, Aircraft.OnStartClient
    // leaves the gear down and the engines off exactly as for a hangar spawn,
    // and the game's own takeoff music plays when the wheels leave the runway.
    //
    // Runway traffic is the game's own (Airbase.Runway, server side):
    //   • A runway counts as free only when nobody is landing on it or on a
    //     runway crossing it, its takeoff queue is empty, and no aircraft is
    //     sitting on it.
    //   • After spawning, the player is put in the runway's takeoff queue.
    //     Taxiing AI check the head of that queue (IsAvailableForTakeoff,
    //     RequestTaxi), so they hold short instead of lining up behind or on
    //     top of the player.
    //   • The player leaves the queue the way an AI does in
    //     AIPilotTakeoffState: RegisterTakeoffLeftRunway once radar altitude
    //     passes 1 m. Leaving the runway on the ground, or the aircraft being
    //     destroyed, releases it too.
    //
    // AI landings pick a runway without looking at the takeoff queue
    // (Airbase.RequestLanding), so nothing here stops an AI from landing on a
    // player who sits on the runway; the check before spawning only avoids
    // spawning in front of one that is already on approach.
    //
    // Carrier runways move, and the spawningHangar = null client path sets only
    // linear velocity, so carriers keep their normal spawn.
    // ──────────────────────────────────────────────────────────────────────────
    internal static class RunwaySpawn
    {
        private static readonly FieldInfo SpawnedObjectField =
            AccessTools.Field(typeof(Hangar), "spawnedObject");

        /// <summary>
        /// Spawns the aircraft on a free runway of the hangar's airbase.
        /// Returns false, having spawned nothing, if there isn't one.
        /// </summary>
        internal static bool TrySpawn(
            Hangar             hangar,
            Player             player,
            AircraftDefinition definition,
            Loadout            loadout,
            float              fuelLevel,
            LiveryKey          livery)
        {
            if (!Applies(hangar, definition)) return false;
            Airbase airbase = hangar.parentAirbase;

            if (!TryPickRunway(airbase, hangar.GetSpawnTransform(), definition,
                    out Airbase.Runway runway, out bool reverse))
                return false;

            var usage = new Airbase.Runway.RunwayUsage(runway, reverse);
            Vector3    direction = usage.GetDirection().normalized;
            Quaternion facing    = Quaternion.LookRotation(direction, Vector3.up);

            // Line up just past the threshold: nose half a fuselage length down
            // the runway from the start point, then apply the same spawn offset
            // and resting attitude a hangar spawn uses (Hangar.SpawnAircraft).
            GlobalPosition position = usage.GetStart().GlobalPosition()
                + direction * (definition.length * 0.5f)
                + facing * Vector3.up      * definition.spawnOffset.y
                + facing * Vector3.forward * definition.spawnOffset.z;
            Quaternion rotation = facing * Quaternion.Euler(definition.restRotation);

            Aircraft aircraft = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(
                player:         player,
                prefab:         definition.unitPrefab,
                loadout:        loadout,
                fuelLevel:      fuelLevel,
                livery:         livery,
                globalPosition: position,
                rotation:       rotation,
                startingVel:    Vector3.zero,
                spawningHangar: null,
                HQ:             hangar.attachedUnit.NetworkHQ,
                uniqueName:     null,
                skill:          1f,
                bravery:        0.5f);

            // Same follow-up as Hangar.SpawnAircraft and the air spawn patch.
            if (loadout == null)
                aircraft.Networkloadout =
                    aircraft.weaponManager.SelectAIAircraftWeapons(hangar.parentAirbase);
            SpawnedObjectField.SetValue(hangar, aircraft.gameObject);

            // Hold the runway, in this direction, for the player.
            runway.SetUsageDirection(reverse);
            runway.QueueTakeoff(aircraft);
            AirSpawnPlugin.Instance!.StartCoroutine(HoldRunway(runway, aircraft));

            AirSpawnPlugin.Log.LogInfo(
                $"[AirSpawn] {definition.unitName}  [runway] {usage.GetName()} at {airbase.name}");
            return true;
        }

        /// <summary>
        /// Whether runway spawn applies at all: a land airbase with at least one
        /// takeoff runway long enough, and an aircraft that needs a takeoff run
        /// (helicopters and other aircraft with no takeoff speed spawn as usual).
        /// </summary>
        internal static bool Applies(Hangar hangar, AircraftDefinition definition)
        {
            Airbase airbase = hangar.parentAirbase;
            if (airbase == null || airbase.runways == null) return false;
            if (hangar.attachedUnit is Ship) return false;
            if (definition.aircraftParameters.takeoffSpeed <= 0f) return false;

            foreach (Airbase.Runway runway in airbase.runways)
                if (IsCandidate(runway, definition)) return true;
            return false;
        }

        internal static bool IsCandidate(Airbase.Runway runway, AircraftDefinition definition) =>
            runway != null && runway.Takeoff &&
            runway.Length >= definition.aircraftParameters.takeoffDistance;

        internal static bool AnyFree(Airbase airbase, AircraftDefinition definition)
        {
            foreach (Airbase.Runway runway in airbase.runways)
                if (IsCandidate(runway, definition) && IsFree(runway)) return true;
            return false;
        }

        /// <summary>
        /// The nearest free takeoff runway long enough for the aircraft, using
        /// the game's own distance and direction choice (Runway.GetDistance
        /// keeps the direction AI have used in the last 30 s).
        /// </summary>
        private static bool TryPickRunway(
            Airbase            airbase,
            Transform          from,
            AircraftDefinition definition,
            out Airbase.Runway runway,
            out bool           reverse)
        {
            runway  = null!;
            reverse = false;
            float best = float.MaxValue;

            foreach (Airbase.Runway candidate in airbase.runways)
            {
                if (!IsCandidate(candidate, definition) || !IsFree(candidate)) continue;

                Airbase.Runway.RunwayDistanceResult distance = candidate.GetDistance(from);
                if (distance.Distance < best)
                {
                    best    = distance.Distance;
                    runway  = candidate;
                    reverse = distance.Reverse;
                }
            }
            return runway != null;
        }

        private static bool IsFree(Airbase.Runway runway)
        {
            // Landings or a queued takeoff, on this runway or one crossing it.
            if (runway.OtherAircraftUsingRunway(null!))                return false;
            if (runway.CrossingRunwaysInUse())                         return false;
            if (!runway.ClearForTakeoff(null!, checkCrossing: true))   return false;

            // Aircraft physically on it: one mid takeoff roll has already left
            // the queue, and a parked or taxiing one was never in it.
            foreach (Aircraft other in UnitRegistry.allAircraft)
            {
                if (other == null || other.disabled) continue;
                if (runway.AircraftOnRunway(other)) return false;
            }
            return true;
        }

        private static IEnumerator HoldRunway(Airbase.Runway runway, Aircraft aircraft)
        {
            var wait = new WaitForSeconds(0.5f);
            yield return wait;

            while (runway.airbase != null
                   && aircraft != null && !aircraft.disabled
                   && aircraft.radarAlt <= 1f
                   && runway.AircraftOnRunway(aircraft))
            {
                yield return wait;
            }

            // Scene unloaded: the runway went with it.
            if (runway.airbase == null) yield break;

            runway.RegisterTakeoffLeftRunway(aircraft);
        }
    }
}
