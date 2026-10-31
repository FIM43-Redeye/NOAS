using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace AirSpawn
{
    // ──────────────────────────────────────────────────────────────────────────
    // Runway queue — players wait in line for a free runway.
    //
    // When runway spawn applies but no runway is free (or other players are
    // already waiting at that airbase), the spawn request is accepted as a
    // delayed spawn, the same mechanism carriers use for their elevators:
    // Hangar.TrySpawnAircraft returns TrySpawnResult(allowed, hangar,
    // delayedSpawn: true), the player's menu closes and they stay "spawn
    // pending" until the aircraft appears. Players are served first come,
    // first served per airbase, on whichever suitable runway frees up first.
    //
    // While anyone is waiting, AI that are holding short are kept there:
    // Runway.IsAvailableForTakeoff answers false for any querier that isn't
    // already in the runway's takeoff queue. AI only join that queue once
    // cleared (AIPilotTaxiState.WaitingForTakeoffClearance), so aircraft
    // already entering the runway, and those on their takeoff roll, carry on
    // and clear it. AI landings carry on too, and the player waits for them.
    //
    // The "waiting for a runway" window is drawn on the server, so the host
    // sees it for their own spawn. A remote client just waits for its aircraft.
    // ──────────────────────────────────────────────────────────────────────────
    internal sealed class RunwayQueue : MonoBehaviour
    {
        private sealed class Waiting
        {
            internal Player             Player     = null!;
            internal Hangar             Hangar     = null!;
            internal Airbase            Airbase    = null!;
            internal AircraftDefinition Definition = null!;
            internal LiveryKey          Livery;
            internal Loadout            Loadout    = null!;
            internal float              FuelLevel;
        }

        private const float CheckInterval = 0.5f;
        private const int   WindowId      = 0x4E4F4153;   // "NOAS"

        private static readonly List<Waiting> Queue = new List<Waiting>();

        /// <summary>
        /// Set while the queue spawns an aircraft itself, so the spawn patches
        /// don't send it back into the queue or onto a runway.
        /// </summary>
        internal static bool Bypass { get; private set; }

        private float _nextCheck;
        private bool  _cursorHeld;

        internal static bool HasWaitingAt(Airbase airbase)
        {
            foreach (Waiting w in Queue)
                if (w.Airbase == airbase) return true;
            return false;
        }

        /// <summary>Whether a waiting player could use this runway.</summary>
        internal static bool IsHoldingFor(Airbase.Runway runway)
        {
            foreach (Waiting w in Queue)
                if (w.Airbase == runway.airbase && RunwaySpawn.IsCandidate(runway, w.Definition))
                    return true;
            return false;
        }

        internal static void Enqueue(
            Hangar hangar, Player player, AircraftDefinition definition,
            LiveryKey livery, Loadout loadout, float fuelLevel)
        {
            Queue.RemoveAll(w => w.Player == player);
            Queue.Add(new Waiting
            {
                Player     = player,
                Hangar     = hangar,
                Airbase    = hangar.parentAirbase,
                Definition = definition,
                Livery     = livery,
                Loadout    = loadout,
                FuelLevel  = fuelLevel,
            });
            AirSpawnPlugin.Log.LogInfo(
                $"[AirSpawn] {definition.unitName}  waiting for a runway at {hangar.parentAirbase.name}");
        }

        // ── Server: spawn the first in line at each airbase once a runway frees

        private void Update()
        {
            if (Queue.Count == 0 || Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + CheckInterval;

            var served = new HashSet<Airbase>();
            for (int i = 0; i < Queue.Count; i++)
            {
                Waiting w = Queue[i];

                // Disconnected, or the scene went away with the airbase.
                if (w.Player == null || w.Airbase == null || w.Hangar == null)
                {
                    if (w.Player != null) w.Player.RpcClearSpawnPending();
                    Queue.RemoveAt(i--);
                    continue;
                }

                // Airbase captured, or the player changed sides.
                if (w.Player.HQ != w.Airbase.CurrentHQ)
                {
                    w.Player.RpcClearSpawnPending();
                    Queue.RemoveAt(i--);
                    continue;
                }

                // First come, first served: only the head at each airbase.
                if (!served.Add(w.Airbase)) continue;

                if (RunwaySpawn.TrySpawn(w.Hangar, w.Player, w.Definition,
                        w.Loadout, w.FuelLevel, w.Livery))
                {
                    w.Player.FlyOwnedAirframe(w.Definition);
                    w.Player.RpcClearSpawnPending();
                    Queue.RemoveAt(i--);
                }
            }
        }

        /// <summary>
        /// Leave the queue and spawn the way the mod would without runway spawn:
        /// in the air if that is enabled, otherwise in a hangar.
        /// </summary>
        private static void SpawnElsewhere(Waiting w)
        {
            Airbase.TrySpawnResult result;
            Bypass = true;
            try
            {
                result = w.Airbase.TrySpawnAircraft(
                    w.Player, w.Definition, w.Livery, w.Loadout, w.FuelLevel);
            }
            finally
            {
                Bypass = false;
            }

            if (!result.Allowed)
            {
                MessageUI.i?.GameMessage("No hangar is free right now. Still waiting for a runway.");
                return;
            }

            Queue.Remove(w);
            if (!result.DelayedSpawn) w.Player.RpcClearSpawnPending();
        }

        // ── Host: the waiting window for the host's own spawn ────────────────

        private void OnGUI()
        {
            Waiting? mine = null;
            if (Queue.Count > 0 && GameManager.GetLocalPlayer<Player>(out Player local))
                mine = Queue.Find(w => w.Player == local && w.Airbase != null);

            // Place in line at this airbase, counting from 1.
            int place = 0;
            if (mine != null)
                foreach (Waiting w in Queue)
                {
                    if (w.Airbase == mine.Airbase) place++;
                    if (w == mine) break;
                }

            // The aircraft menu has closed, so take the cursor while waiting.
            if ((mine != null) != _cursorHeld)
            {
                _cursorHeld = mine != null;
                CursorManager.SetFlag(CursorFlags.Dialogue, _cursorHeld);
            }
            if (mine == null) return;

            const float width = 380f;
            var rect = new Rect((Screen.width - width) / 2f, Screen.height * 0.2f, width, 0f);
            GUILayout.Window(WindowId, rect, _ => DrawWindow(mine, place), "Waiting for a runway");
        }

        private static void DrawWindow(Waiting mine, int place)
        {
            string airbaseName = mine.Airbase.SavedAirbase.DisplayName;
            GUILayout.Label(place <= 1
                ? $"You're next in line for a runway at {airbaseName}."
                : $"You're number {place} in line for a runway at {airbaseName}.");
            GUILayout.Label("Waiting AI aircraft will hold short until you've taken off.");

            string instead = AirSpawnPlugin.Instance!.Settings.EnableAirSpawn.Value
                ? "Spawn in the air instead"
                : "Spawn in a hangar instead";
            if (GUILayout.Button(instead)) SpawnElsewhere(mine);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Patch 4 — Hangar.TrySpawnAircraft (server)
    //
    // Sends a player's spawn into the runway queue, as a delayed spawn, when no
    // suitable runway is free or other players are already waiting there.
    // When a runway is free and nobody is waiting, the original runs and
    // Patch_Hangar_SpawnAircraft puts the aircraft straight on the runway.
    // ──────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(Hangar), nameof(Hangar.TrySpawnAircraft))]
    internal static class Patch_Hangar_TrySpawnAircraft
    {
        private static bool Prefix(
            Hangar                     __instance,
            Player                     player,
            AircraftDefinition         definition,
            LiveryKey                  livery,
            Loadout                    loadout,
            float                      fuelLevel,
            ref Airbase.TrySpawnResult __result)
        {
            if (player == null || RunwayQueue.Bypass) return true;
            if (!AirSpawnPlugin.Instance!.Settings.EnableRunwaySpawn.Value) return true;

            // Let the original refuse a busy or unsuitable hangar as usual, so
            // Airbase.TrySpawnAircraft moves on to the next one.
            if (!__instance.CanSpawnAircraft(definition)) return true;
            if (!RunwaySpawn.Applies(__instance, definition)) return true;

            Airbase airbase = __instance.parentAirbase;
            if (!RunwayQueue.HasWaitingAt(airbase) && RunwaySpawn.AnyFree(airbase, definition))
                return true;

            RunwayQueue.Enqueue(__instance, player, definition, livery, loadout, fuelLevel);
            __result = new Airbase.TrySpawnResult(allowed: true, __instance, delayedSpawn: true);
            return false;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Patch 5 — Airbase.Runway.IsAvailableForTakeoff (server)
    //
    // Keeps AI that are holding short where they are while a player waits for
    // this runway. Aircraft already in the takeoff queue (cleared and entering
    // the runway) are let through so they can clear it.
    // ──────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(Airbase.Runway), nameof(Airbase.Runway.IsAvailableForTakeoff))]
    internal static class Patch_Runway_IsAvailableForTakeoff
    {
        private static void Postfix(
            Airbase.Runway  __instance,
            Aircraft        querier,
            Queue<Aircraft> ___takeoffQueue,
            ref bool        __result)
        {
            if (!__result) return;
            if (___takeoffQueue != null && ___takeoffQueue.Contains(querier)) return;
            if (RunwayQueue.IsHoldingFor(__instance)) __result = false;
        }
    }
}
