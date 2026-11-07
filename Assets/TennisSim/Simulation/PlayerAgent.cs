using System;
using UnityEngine;
using Random = System.Random;

namespace TennisSim.Simulation
{
    /// <summary>One player on court: turns an intent into movement and shots. Knows nothing about who produced the intent.</summary>
    public sealed class PlayerAgent
    {
        const float StickActive = 0.2f;
        const float NetZoneDistance = 5f;
        const float RecoveryBehindBaseline = 0.6f;
        const float VolleyRecoveryDistance = 3.2f;

        readonly PlayerMovementTuning movementTuning;

        bool recovering;
        Vector2 aim;

        public PlayerSlot Slot { get; }
        public PlayerMovement Movement { get; }
        public PlayerShotController Shot { get; }
        public IPlayerIntentSource IntentSource { get; set; }
        /// <summary>When on, a preparing player runs to the contact point and the stick only aims. When off, control is fully manual.</summary>
        public bool AutoPositioning { get; set; } = true;

        public Vector3 Position => Movement.Position;
        /// <summary>The player asked to put the ball in play this tick.</summary>
        public bool ServeRequested { get; private set; }

        public PlayerAgent(PlayerSlot slot, PlayerMovementTuning movementTuning, ShotTuning shotTuning, in BallPhysicsParams physics, Random rng)
        {
            Slot = slot;
            this.movementTuning = movementTuning;
            Movement = new PlayerMovement(slot, movementTuning);
            Shot = new PlayerShotController(slot, shotTuning, movementTuning, physics, rng);
        }

        public void ResetForPoint(Vector3 position)
        {
            Movement.Teleport(position);
            Shot.ResetForPoint();
            recovering = false;
            aim = Vector2.zero;
        }

        public ShotResult? Tick(in PlayerPerception p, float dt)
        {
            PlayerIntent intent = IntentSource != null ? IntentSource.ReadIntent(p) : default;

            ServeRequested = p.AwaitingServe && p.IsServer && intent.Pressed.HasValue;
            UpdateAim(intent);

            float sprintSpeed = intent.Sprint ? movementTuning.SprintSpeed : movementTuning.RunSpeed;
            ShotResult? shot = Shot.Tick(p, intent, Movement.Velocity, sprintSpeed, aim);

            if (intent.Pressed.HasValue || intent.Move.sqrMagnitude > StickActive * StickActive) recovering = false;
            if (shot.HasValue) recovering = true;

            Vector3 desired = p.AwaitingServe && p.IsServer ? Vector3.zero : DesiredVelocity(intent, sprintSpeed);
            Movement.Tick(desired, dt);
            return shot;
        }

        void UpdateAim(in PlayerIntent intent)
        {
            // A dedicated aim stick wins; otherwise the movement stick doubles as aim, as on a keyboard.
            aim = intent.Aim.sqrMagnitude > StickActive * StickActive ? intent.Aim : intent.Move;
        }

        Vector3 DesiredVelocity(in PlayerIntent intent, float speed)
        {
            bool preparing = Shot.Phase == SwingPhase.Preparing;
            bool committed = preparing || Shot.Phase == SwingPhase.Swinging;

            if (AutoPositioning && committed && Shot.Plan.Valid)
                return Movement.ArriveVelocity(Shot.Plan.Stance, speed);

            if (intent.Move.sqrMagnitude > StickActive * StickActive)
            {
                Vector2 move = Vector2.ClampMagnitude(intent.Move, 1f);
                float scaled = speed * (preparing ? movementTuning.PreparingSpeedScale : 1f);
                return new Vector3(move.x, 0f, move.y) * scaled;
            }

            if (recovering)
                return Movement.ArriveVelocity(RecoveryPoint(), movementTuning.RunSpeed * movementTuning.RecoverySpeedScale);

            return Vector3.zero;
        }

        /// <summary>The ready position: behind the baseline middle, or at the net if that is where the player has come in to.</summary>
        Vector3 RecoveryPoint()
        {
            float forward = CourtGeometry.ForwardSign(Slot);
            float distanceFromNet = Mathf.Abs(Position.z);
            float recovery = distanceFromNet < NetZoneDistance
                ? VolleyRecoveryDistance
                : CourtGeometry.HalfLength + RecoveryBehindBaseline;
            return new Vector3(0f, 0f, -recovery * forward);
        }
    }
}
