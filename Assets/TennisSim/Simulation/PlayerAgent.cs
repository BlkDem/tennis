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
        /// <summary>Spare time left when auto-positioning takes over, so the player arrives and can still aim.</summary>
        const float ContactCommitMargin = 0.25f;

        readonly PlayerMovementTuning movementTuning;
        readonly ShotTuning shotTuning;

        bool recovering;
        bool committedToContact;
        int committedRevision;
        Vector2 aim;

        public PlayerSlot Slot { get; }
        public PlayerMovement Movement { get; }
        public PlayerShotController Shot { get; }
        public ServeController Serve { get; }
        public IPlayerIntentSource IntentSource { get; set; }
        /// <summary>
        /// When on, the stick still moves a preparing player, and only at the last moment, or with the stick
        /// neutral, does the player run to the contact point by themselves. When off, control is fully manual.
        /// </summary>
        public bool AutoPositioning { get; set; } = true;

        public Vector3 Position => Movement.Position;
        public ShotTuning ShotTuning => shotTuning;

        public PlayerAgent(PlayerSlot slot, PlayerMovementTuning movementTuning, ShotTuning shotTuning, in BallPhysicsParams physics, Random rng)
        {
            Slot = slot;
            this.movementTuning = movementTuning;
            this.shotTuning = shotTuning;
            Movement = new PlayerMovement(slot, movementTuning);
            Shot = new PlayerShotController(slot, shotTuning, movementTuning, physics, rng);
            Serve = new ServeController(slot, shotTuning, physics, rng);
        }

        public void ResetForPoint(Vector3 position)
        {
            Movement.Teleport(position);
            Shot.ResetForPoint();
            Serve.Reset();
            recovering = false;
            committedToContact = false;
            aim = Vector2.zero;
        }

        public ShotResult? Tick(in PlayerPerception p, float dt)
        {
            PlayerIntent intent = IntentSource != null ? IntentSource.ReadIntent(p) : default;
            UpdateAim(intent);

            if (p.IsServer && (p.AwaitingServe || p.Tossing)) return TickServing(p, intent, dt);

            float sprintSpeed = intent.Sprint ? movementTuning.SprintSpeed : movementTuning.RunSpeed;
            bool stickActive = intent.Move.sqrMagnitude > StickActive * StickActive;
            UpdateContactCommitment(p, sprintSpeed);
            bool steering = stickActive && (!AutoPositioning || !committedToContact);

            ShotResult? shot = Shot.Tick(p, intent, Movement.Velocity, sprintSpeed, aim, steering);

            if (intent.Pressed.HasValue || stickActive) recovering = false;
            if (shot.HasValue) recovering = true;

            Movement.Tick(DesiredVelocity(intent, sprintSpeed, steering), dt);
            return shot;
        }

        /// <summary>Before the toss the stick walks the server along the baseline; once the ball is up they stand still.</summary>
        ShotResult? TickServing(in PlayerPerception p, in PlayerIntent intent, float dt)
        {
            ShotResult? serve = Serve.Tick(p, intent, aim);

            bool walking = p.AwaitingServe && Serve.Phase == ServePhase.Idle && Mathf.Abs(intent.Move.x) > StickActive;
            Vector3 desired = walking ? new Vector3(Mathf.Clamp(intent.Move.x, -1f, 1f) * shotTuning.ServerMoveSpeed, 0f, 0f) : Vector3.zero;
            Movement.Tick(desired, dt);

            recovering = serve.HasValue;
            return serve;
        }

        /// <summary>Latches once the player has to start running to make the contact, so control does not flicker.</summary>
        void UpdateContactCommitment(in PlayerPerception p, float speed)
        {
            bool committedPhase = Shot.Phase == SwingPhase.Preparing || Shot.Phase == SwingPhase.Swinging;
            if (!committedPhase || !Shot.Plan.Valid || committedRevision != p.BallRevision)
            {
                committedToContact = false;
                committedRevision = p.BallRevision;
            }
            if (committedToContact || !committedPhase || !Shot.Plan.Valid) return;

            float travelTime = Vector3.Distance(Position, Shot.Plan.Stance) / Mathf.Max(speed, 0.1f);
            committedToContact = Shot.Phase == SwingPhase.Swinging || travelTime + ContactCommitMargin >= Shot.Plan.Time - p.Now;
        }

        void UpdateAim(in PlayerIntent intent)
        {
            // A dedicated aim stick wins; otherwise the movement stick doubles as aim, as on a keyboard.
            aim = intent.Aim.sqrMagnitude > StickActive * StickActive ? intent.Aim : intent.Move;
        }

        Vector3 DesiredVelocity(in PlayerIntent intent, float speed, bool steering)
        {
            bool preparing = Shot.Phase == SwingPhase.Preparing;
            bool committed = preparing || Shot.Phase == SwingPhase.Swinging;

            if (AutoPositioning && committed && Shot.Plan.Valid && !steering)
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
