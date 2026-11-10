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
        const float ApproachReplanInterval = 0.1f;

        readonly PlayerMovementTuning movementTuning;
        readonly ShotTuning shotTuning;

        bool recovering;
        bool committedToContact;
        int committedRevision;
        Vector2 aim;
        ContactPlan approach;
        int approachRevision = -1;
        float nextApproachPlan;

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

        /// <summary>
        /// A human player gets the footwork a real player does without thinking: with the stick let go they walk up to
        /// a ball coming near them, a held shot sprints when the ball is far, and a shot button let go too early waits
        /// for the ball rather than swinging at the air. Bots play without it.
        /// </summary>
        public bool Assisted
        {
            get => Shot.ForgiveEarlyRelease;
            set => Shot.ForgiveEarlyRelease = value;
        }

        /// <summary>Where the player is walking up to by themselves, before any button is pressed; for the view and checks.</summary>
        public ContactPlan Approach => approach;

        public Vector3 Position => Movement.Position;
        public ShotTuning ShotTuning => shotTuning;

        /// <summary>The keyboard's aim cursor is out: a shot or a serve is held, and the movement keys move the cursor.</summary>
        public bool AimingWithCursor { get; private set; }
        /// <summary>Where the aim cursor is, world X and Z: on the other half, or in the service box on a serve.</summary>
        public Vector2 AimTarget { get; private set; }
        /// <summary>Between points the session walks the player to their next place; their own input waits.</summary>
        public Vector3? WalkTarget { get; set; }
        /// <summary>A brisk walk, the fastest the walk clip plays: any faster and the player jogs back to place.</summary>
        public const float BetweenPointsWalkSpeed = 2f;

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
            WalkTarget = null;
            recovering = false;
            committedToContact = false;
            aim = Vector2.zero;
            AimingWithCursor = false;
            approach = default;
            approachRevision = -1;
        }

        public ShotResult? Tick(in PlayerPerception p, float dt)
        {
            PlayerIntent intent = IntentSource != null ? IntentSource.ReadIntent(p) : default;
            if (WalkTarget.HasValue)
            {
                // A shot still being prepared when the point ended is put away.
                Shot.ResetForPoint();
                AimingWithCursor = false;
                Movement.Tick(Movement.ArriveVelocity(WalkTarget.Value, BetweenPointsWalkSpeed), dt);
                return null;
            }
            UpdateAim(intent);

            if (p.IsServer && (p.AwaitingServe || p.Tossing)) return TickServing(p, intent, dt);

            // With the cursor out the movement keys aim, so for moving the player they count as let go: the player
            // runs to the ball by themselves.
            bool cursor = UpdateShotCursor(intent, dt);
            PlayerIntent movement = intent;
            if (cursor) movement.Move = Vector2.zero;

            float sprintSpeed = intent.Sprint ? movementTuning.SprintSpeed : movementTuning.RunSpeed;
            bool stickActive = movement.Move.sqrMagnitude > StickActive * StickActive;
            UpdateContactCommitment(p, sprintSpeed);
            bool steering = stickActive && (!AutoPositioning || !committedToContact);
            UpdateApproach(p, stickActive);

            ShotResult? shot = Shot.Tick(p, intent, Movement.Velocity, sprintSpeed, aim, steering, cursor ? AimTarget : (Vector2?)null);

            if (intent.Pressed.HasValue || stickActive) recovering = false;
            if (shot.HasValue) recovering = true;

            Movement.Tick(DesiredVelocity(movement, sprintSpeed, steering), dt);
            return shot;
        }

        /// <summary>
        /// The aim cursor of keyboard play, out while a shot is held and the player knows where the ball will be met.
        /// It starts where the shot would go with no aim and moves with the movement keys, kept to where the chosen
        /// shot can land. Without auto-positioning the keys must move the player, so there is no cursor.
        /// </summary>
        bool UpdateShotCursor(in PlayerIntent intent, float dt)
        {
            bool holding = Shot.Phase == SwingPhase.Preparing || Shot.Phase == SwingPhase.Swinging;
            bool cursor = intent.AimCursor && AutoPositioning && holding && Shot.Plan.Valid;
            if (cursor)
            {
                ShotProfile profile = shotTuning.Profile(KindFor(Shot.ActiveButton));
                Vector2 target = AimingWithCursor ? AimTarget : ShotPlanner.DefaultTarget(Slot, profile, Shot.Plan.BallPoint.x);
                AimTarget = ShotPlanner.ClampToProfile(MoveCursor(target, intent, dt), Slot, profile);
            }
            AimingWithCursor = cursor;
            return cursor;
        }

        /// <summary>During the toss the cursor picks the spot in the service box; it starts in the middle, at the usual depth.</summary>
        bool UpdateServeCursor(ServiceBox box, in PlayerIntent intent, float dt)
        {
            bool cursor = intent.AimCursor && (Serve.Phase == ServePhase.Tossing || Serve.Phase == ServePhase.Swinging);
            if (cursor)
            {
                ShotProfile profile = shotTuning.Profile(ServeController.KindFor(Serve.Button));
                Vector2 target = AimingWithCursor ? AimTarget : new Vector2(box.CenterX, profile.DefaultDepth * CourtGeometry.ForwardSign(Slot));
                AimTarget = ShotPlanner.ClampToServeBox(MoveCursor(target, intent, dt), Slot, box, profile, shotTuning);
            }
            AimingWithCursor = cursor;
            return cursor;
        }

        /// <summary>Either stick moves the cursor; the dedicated aim stick wins when both are pushed.</summary>
        Vector2 MoveCursor(Vector2 target, in PlayerIntent intent, float dt)
        {
            Vector2 push = intent.Aim.sqrMagnitude > StickActive * StickActive ? intent.Aim : intent.Move;
            if (push.sqrMagnitude <= StickActive * StickActive) return target;
            return target + Vector2.ClampMagnitude(push, 1f) * (shotTuning.AimCursorSpeed * dt);
        }

        /// <summary>The shot a button usually makes, for where its cursor may go; the contact itself decides the final kind.</summary>
        static ShotKind KindFor(ShotButton button)
        {
            switch (button)
            {
                case ShotButton.Slice: return ShotKind.Slice;
                case ShotButton.Flat: return ShotKind.Flat;
                case ShotButton.Lob: return ShotKind.Lob;
                case ShotButton.Drop: return ShotKind.Drop;
                case ShotButton.Volley: return ShotKind.Volley;
                default: return ShotKind.Topspin;
            }
        }

        /// <summary>Before the toss the stick walks the server along the baseline; once the ball is up they stand still.</summary>
        ShotResult? TickServing(in PlayerPerception p, in PlayerIntent intent, float dt)
        {
            bool cursor = UpdateServeCursor(p.ServeBox, intent, dt);
            ShotResult? serve = Serve.Tick(p, intent, aim, cursor ? AimTarget : (Vector2?)null);

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

        /// <summary>
        /// Before any button is pressed, an assisted player with the stick let go walks up to a ball that will come
        /// near: to the same contact point a held shot would run to. A ball further away is the player's to chase.
        /// </summary>
        void UpdateApproach(in PlayerPerception p, bool stickActive)
        {
            bool helping = Assisted && AutoPositioning && !stickActive && Shot.Phase == SwingPhase.Idle
                           && p.CanHit && !p.AwaitingServe && p.Trajectory != null;
            if (!helping)
            {
                approach = default;
                approachRevision = -1;
                return;
            }
            if (approachRevision == p.BallRevision && p.Now < nextApproachPlan) return;

            approachRevision = p.BallRevision;
            nextApproachPlan = p.Now + ApproachReplanInterval;
            ContactPlan plan = ContactPlanner.Find(p.Trajectory, Position, Slot, null, ShotButton.Auto, p.Now, movementTuning.RunSpeed,
                movementTuning, shotTuning, p.MustLetBounce);
            bool near = plan.Valid && plan.Reachable && Vector3.Distance(Position, plan.Stance) <= movementTuning.ApproachRadius;
            approach = near ? plan : default;
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
            {
                // A human's held shot goes all out for a ball they could not reach at a run, as they would themselves.
                float runTo = Assisted && !Shot.Plan.Reachable ? Mathf.Max(speed, movementTuning.SprintSpeed) : speed;
                return Movement.ArriveVelocity(Shot.Plan.Stance, runTo);
            }

            if (intent.Move.sqrMagnitude > StickActive * StickActive)
            {
                Vector2 move = Vector2.ClampMagnitude(intent.Move, 1f);
                float scaled = speed * (preparing ? movementTuning.PreparingSpeedScale : 1f);
                return new Vector3(move.x, 0f, move.y) * scaled;
            }

            if (approach.Valid)
                return Movement.ArriveVelocity(approach.Stance, speed);

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
