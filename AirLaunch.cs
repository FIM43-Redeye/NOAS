using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace AirSpawn
{
    // ──────────────────────────────────────────────────────────────────────────
    // Patch 2 — Aircraft.OnStartClient (private)
    //
    // Turns an air spawn into an air launch: the aircraft starts held in place
    // with its gear down, is accelerated straight ahead like a catapult shot
    // until it reaches the spawn airspeed, then is released and raises its gear.
    //
    // Spawning directly at speed skips everything the game and its aircraft
    // expect to see on the way from rest to flight:
    //   • The takeoff music is played from Aircraft.CheckRadarAlt only when the
    //     aircraft first becomes airborne with its gear down, but OnStartClient
    //     raises the gear of an aircraft spawned in the air.
    //   • HighLiftDevice (speed-scheduled flaps and slats) only animates when
    //     its target changes, and an aircraft spawned above retract speed
    //     starts at its target, so its parts keep their authored pose.
    // Rather than patching each of those, the launch replays the real
    // sequence, so any part that reacts to speed or gear, modded ones included,
    // sees what it would on a normal takeoff.
    //
    // Physics runs on the owner's machine, so this does too, and only for the
    // local player's own aircraft. A multiplayer client without this mod gets
    // the plain air spawn at full speed.
    // ──────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(Aircraft), "OnStartClient")]
    internal static class Patch_Aircraft_OnStartClient_AirLaunch
    {
        private static void Postfix(Aircraft __instance)
        {
            AirSpawnConfig cfg = AirSpawnPlugin.Instance!.Settings;
            if (!cfg.EnableAirSpawn.Value) return;

            Player? player = __instance.Player;
            if (player == null || !player.IsLocalPlayer || !__instance.LocalSim) return;

            // A hangar spawn keeps its hangar; air spawns have none.
            if (__instance.NetworkspawningHangar != null) return;

            // Same airborne-at-spawn test OnStartClient uses to raise the gear.
            if (__instance.radarAlt <= __instance.definition.spawnOffset.y + 1f) return;

            AirLaunch.Begin(__instance, __instance.NetworkstartingVelocity,
                cfg.LaunchAccelerationG.Value * 9.81f);
        }
    }

    internal sealed class AirLaunch : MonoBehaviour
    {
        private Aircraft   _aircraft = null!;
        private Quaternion _rotation;
        private Vector3    _direction;
        private float      _targetSpeed;
        private float      _acceleration;   // m/s²
        private float      _speed;

        internal static void Begin(Aircraft aircraft, Vector3 spawnVelocity, float acceleration)
        {
            float targetSpeed = spawnVelocity.magnitude;
            if (targetSpeed < 1f) return;

            // Put the gear back down the way a ground spawn initialises it:
            // from Uninitialized, GearStateChanged snaps straight to
            // LockedExtended with no extension animation.
            aircraft.SetGear(LandingGear.GearState.Uninitialized);
            aircraft.SetGear(deployed: true);

            // Start at rest before the first physics step, so the aircraft
            // never simulates a frame at spawn speed followed by a sudden stop.
            aircraft.rb.velocity        = Vector3.zero;
            aircraft.rb.angularVelocity = Vector3.zero;

            AirLaunch launch = aircraft.gameObject.AddComponent<AirLaunch>();
            launch._aircraft     = aircraft;
            launch._rotation     = aircraft.transform.rotation;
            launch._direction    = spawnVelocity / targetSpeed;
            launch._targetSpeed  = targetSpeed;
            launch._acceleration = acceleration;
        }

        private void FixedUpdate()
        {
            // Destroyed, or the pilot ejected: leave the airframe to physics.
            if (_aircraft == null || _aircraft.disabled || _aircraft.Player == null)
            {
                Destroy(this);
                return;
            }

            Rigidbody rb = _aircraft.rb;
            _speed = Mathf.Min(_speed + _acceleration * Time.fixedDeltaTime, _targetSpeed);
            rb.angularVelocity = Vector3.zero;

            if (_speed >= _targetSpeed)
            {
                rb.velocity = _direction * _targetSpeed;
                _aircraft.SetGear(deployed: false);
                Destroy(this);
                return;
            }

            // Hold attitude and height: the physics step adds gravity to the
            // velocity before moving the body, so pre-subtract it.
            Vector3 velocity = _direction * _speed;
            if (rb.useGravity) velocity -= Physics.gravity * Time.fixedDeltaTime;
            rb.velocity = velocity;
            rb.MoveRotation(_rotation);
        }
    }
}
