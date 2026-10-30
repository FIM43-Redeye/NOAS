using System.Collections.Generic;
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
    // The owner simulates its aircraft with complex physics
    // (Aircraft.SetComplexPhysics): every AeroPart has its own Rigidbody,
    // joined to its parent by a breakable FixedJoint. So the launch moves every
    // part's body together as one rigid motion; moving only Aircraft.rb would
    // tear the airframe apart at the joints.
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
        private float      _heldFor;        // s

        // The game's own "has left the ground" flag, set by CheckRadarAlt,
        // which also starts the takeoff music when the gear is down.
        private static readonly AccessTools.FieldRef<Aircraft, bool> Airborne =
            AccessTools.FieldRefAccess<Aircraft, bool>("airborne");

        // CheckRadarAlt first runs 0.1 s after spawning; never hold longer than
        // this waiting for it.
        private const float MaxHoldForAirborne = 1f;

        // How quickly an attitude error is turned back, in seconds.
        private const float AttitudeSettleTime = 0.25f;

        private readonly List<Rigidbody> _bodies = new List<Rigidbody>();

        internal static void Begin(Aircraft aircraft, Vector3 spawnVelocity, float acceleration)
        {
            // A spawn at rest (a helicopter, say) still gets the gear-down start,
            // released as soon as the game has seen it airborne.
            float targetSpeed = spawnVelocity.magnitude;
            Vector3 direction = targetSpeed > 0.01f
                ? spawnVelocity / targetSpeed
                : aircraft.transform.forward;

            // Put the gear back down the way a ground spawn initialises it:
            // from Uninitialized, GearStateChanged snaps straight to
            // LockedExtended with no extension animation.
            aircraft.SetGear(LandingGear.GearState.Uninitialized);
            aircraft.SetGear(deployed: true);

            AirLaunch launch = aircraft.gameObject.AddComponent<AirLaunch>();
            launch._aircraft     = aircraft;
            launch._rotation     = aircraft.transform.rotation;
            launch._direction    = direction;
            launch._targetSpeed  = targetSpeed;
            launch._acceleration = acceleration;

            // Start at rest before the first physics step, so the aircraft
            // never simulates a frame at spawn speed followed by a sudden stop.
            launch.SetMotion(Vector3.zero, Vector3.zero, holdAgainstGravity: false);
        }

        /// <summary>
        /// Gives every attached part the velocity it would have if the whole
        /// aircraft were one rigid body moving at <paramref name="velocity"/> and
        /// turning at <paramref name="angularVelocity"/>, so no joint is strained.
        /// </summary>
        private void SetMotion(Vector3 velocity, Vector3 angularVelocity, bool holdAgainstGravity)
        {
            // Re-collected each time: the game switches between simple and
            // complex physics on its own (Aircraft.CheckPhysicsLod).
            _bodies.Clear();
            _bodies.Add(_aircraft.rb);
            foreach (UnitPart part in _aircraft.partLookup)
            {
                if (part == null || part.IsDetached()) continue;
                if (part.rb != null && !_bodies.Contains(part.rb)) _bodies.Add(part.rb);
            }

            Vector3 pivot = _aircraft.rb.worldCenterOfMass;
            foreach (Rigidbody body in _bodies)
            {
                Vector3 v = velocity + Vector3.Cross(angularVelocity, body.worldCenterOfMass - pivot);

                // The physics step adds gravity to the velocity before moving
                // the body, so pre-subtract it to hold height.
                if (holdAgainstGravity && body.useGravity)
                    v -= Physics.gravity * Time.fixedDeltaTime;

                body.velocity        = v;
                body.angularVelocity = angularVelocity;
            }
        }

        private void FixedUpdate()
        {
            // Destroyed, or the pilot ejected: leave the airframe to physics.
            if (_aircraft == null || _aircraft.disabled || _aircraft.Player == null)
            {
                Destroy(this);
                return;
            }

            _speed = Mathf.Min(_speed + _acceleration * Time.fixedDeltaTime, _targetSpeed);
            _heldFor += Time.fixedDeltaTime;

            bool seenAirborne = Airborne(_aircraft) || _heldFor >= MaxHoldForAirborne;
            if (_speed >= _targetSpeed && seenAirborne)
            {
                SetMotion(_direction * _targetSpeed, Vector3.zero, holdAgainstGravity: false);
                _aircraft.SetGear(deployed: false);
                Destroy(this);
                return;
            }

            // Hold attitude by turning the whole aircraft back toward its spawn
            // rotation (aerodynamic forces nudge it a little each step).
            Quaternion error = _rotation * Quaternion.Inverse(_aircraft.rb.rotation);
            error.ToAngleAxis(out float angleDeg, out Vector3 axis);
            if (angleDeg > 180f) angleDeg -= 360f;
            Vector3 turn = Mathf.Abs(angleDeg) > 0.01f && !float.IsNaN(axis.x)
                ? axis * (angleDeg * Mathf.Deg2Rad / AttitudeSettleTime)
                : Vector3.zero;

            SetMotion(_direction * _speed, turn, holdAgainstGravity: true);
        }
    }
}
