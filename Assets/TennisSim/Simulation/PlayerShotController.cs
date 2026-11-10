using System;
using UnityEngine;
using Random = System.Random;

namespace TennisSim.Simulation
{
    public enum SwingPhase { Idle, Preparing, Swinging, Recovering }

    /// <summary>
    /// The timing heart of the game. Press = prepare, release = swing, and the racket reaches its sweet spot
    /// a fixed time later. Holding through the ball auto-swings slightly late: always playable, never best.
    /// The longer the preparation, the more power the shot is charged with.
    /// </summary>
    public sealed class PlayerShotController
    {
        const float MinSpeedForClosestApproach = 3f;
        const float MinHorizontalSpeedForLeaving = 3f;
        const float SteeringReplanInterval = 0.1f;

        readonly PlayerSlot slot;
        readonly ShotTuning tuning;
        readonly PlayerMovementTuning movementTuning;
        readonly BallPhysicsParams physics;
        readonly Random rng;

        int plannedRevision = -1;
        float preparationStart;
        float swingStart;
        float phaseEnd;
        float preparationHeld;
        float previousDistance;
        float nextSteeringReplan;
        float lastNow;
        bool hasPreviousDistance;
        ShotButton? bufferedPress;
        bool releasedEarly;
        float heldBeforeRelease;

        public SwingPhase Phase { get; private set; }
        /// <summary>
        /// Letting go well before the ball arrives keeps the shot ready instead of swinging at the air: the racket goes
        /// through on its own, a little late, with the power charged so far. For human players.
        /// </summary>
        public bool ForgiveEarlyRelease { get; set; }
        /// <summary>The button was let go early and the shot waits for the ball by itself.</summary>
        public bool WaitingForBall => Phase == SwingPhase.Preparing && releasedEarly;
        /// <summary>When the current or last swing started; the racket is at its sweet spot <see cref="ShotTuning.SwingLatency"/> later.</summary>
        public float SwingStart => swingStart;
        public ShotButton ActiveButton { get; private set; }
        public ContactPlan Plan { get; private set; }
        public HandSide DisplaySide { get; private set; }

        /// <summary>
        /// 0..1: how far holding the button has charged the shot's power. It grows while preparing and is fixed when
        /// the swing starts; the next shot starts again from nothing.
        /// </summary>
        public float Charge
        {
            get
            {
                float full = Mathf.Max(tuning.FullPreparationTime, 1e-3f);
                if (Phase == SwingPhase.Preparing) return Mathf.Clamp01((releasedEarly ? heldBeforeRelease : lastNow - preparationStart) / full);
                if (Phase == SwingPhase.Swinging) return Mathf.Clamp01(preparationHeld / full);
                return 0f;
            }
        }

        public PlayerShotController(PlayerSlot slot, ShotTuning tuning, PlayerMovementTuning movementTuning, in BallPhysicsParams physics, Random rng)
        {
            this.slot = slot;
            this.tuning = tuning;
            this.movementTuning = movementTuning;
            this.physics = physics;
            this.rng = rng;
        }

        public void ResetForPoint()
        {
            Phase = SwingPhase.Idle;
            Plan = default;
            plannedRevision = -1;
            bufferedPress = null;
            hasPreviousDistance = false;
            releasedEarly = false;
        }

        /// <summary>Returns a shot when the racket met the ball this tick.</summary>
        /// <param name="playerSteering">The stick is moving the player, so the contact point has to follow them.</param>
        /// <param name="target">A point picked with the aim cursor, world X and Z; replaces <paramref name="aim"/> when set.</param>
        public ShotResult? Tick(in PlayerPerception p, in PlayerIntent intent, Vector3 velocity, float maxSpeed, Vector2 aim, bool playerSteering = false,
            Vector2? target = null)
        {
            lastNow = p.Now;
            HandleButtons(p, intent, maxSpeed);

            switch (Phase)
            {
                case SwingPhase.Preparing:
                    TickPreparing(p, maxSpeed, playerSteering);
                    break;
                case SwingPhase.Swinging:
                    return TickSwinging(p, velocity, aim, target);
                case SwingPhase.Recovering:
                    if (p.Now >= phaseEnd)
                    {
                        Phase = SwingPhase.Idle;
                        if (bufferedPress.HasValue)
                        {
                            BeginPreparing(bufferedPress.Value, p, maxSpeed);
                            bufferedPress = null;
                        }
                    }
                    break;
            }
            return null;
        }

        void HandleButtons(in PlayerPerception p, in PlayerIntent intent, float maxSpeed)
        {
            if (intent.Pressed.HasValue)
            {
                if (Phase == SwingPhase.Idle || Phase == SwingPhase.Preparing)
                    BeginPreparing(intent.Pressed.Value, p, maxSpeed);
                else
                    bufferedPress = intent.Pressed;
            }

            if (intent.Released.HasValue)
            {
                if (bufferedPress == intent.Released) bufferedPress = null;
                if (Phase == SwingPhase.Preparing && intent.Released == ActiveButton && !releasedEarly)
                {
                    if (ForgiveEarlyRelease && TooEarlyToSwing(p.Now))
                    {
                        releasedEarly = true;
                        heldBeforeRelease = p.Now - preparationStart;
                    }
                    else StartSwing(p.Now);
                }
            }
        }

        /// <summary>
        /// Let go further ahead of the ball than an auto-swing comes late, the swing would hit nothing; or no ball is
        /// coming yet. Closer than that, the release is the swing and its timing is graded.
        /// </summary>
        bool TooEarlyToSwing(float now) => !Plan.Valid || Plan.Time - now > tuning.SwingLatency + tuning.AutoSwingLateness;

        void BeginPreparing(ShotButton button, in PlayerPerception p, float maxSpeed)
        {
            // Pressed again after an early release: the hold, and its charge, starts over.
            if (Phase != SwingPhase.Preparing || releasedEarly) preparationStart = p.Now;
            releasedEarly = false;
            Phase = SwingPhase.Preparing;
            ActiveButton = button;
            Replan(p, maxSpeed);
        }

        void TickPreparing(in PlayerPerception p, float maxSpeed, bool playerSteering)
        {
            if (plannedRevision != p.BallRevision || (playerSteering && p.Now >= nextSteeringReplan))
            {
                Replan(p, maxSpeed);
                nextSteeringReplan = p.Now + SteeringReplanInterval;
            }
            if (Plan.Valid && Plan.Time - p.Now <= tuning.SwingLatency - tuning.AutoSwingLateness)
                StartSwing(p.Now);
        }

        void Replan(in PlayerPerception p, float maxSpeed)
        {
            plannedRevision = p.BallRevision;
            Plan = p.CanHit && !p.AwaitingServe && p.Trajectory != null
                ? ContactPlanner.Find(p.Trajectory, p.Position, slot, ForcedSide(ActiveButton), ActiveButton, p.Now, maxSpeed, movementTuning, tuning, p.MustLetBounce)
                : default;
            if (Plan.Valid) DisplaySide = Plan.Side;
        }

        void StartSwing(float now)
        {
            preparationHeld = releasedEarly ? heldBeforeRelease : now - preparationStart;
            releasedEarly = false;
            swingStart = now;
            hasPreviousDistance = false;
            Phase = SwingPhase.Swinging;
        }

        ShotResult? TickSwinging(in PlayerPerception p, Vector3 velocity, Vector2 aim, Vector2? target)
        {
            float sweetTime = swingStart + tuning.SwingLatency;

            if (p.CanHit && !p.AwaitingServe && !p.MustLetBounce)
            {
                HandSide side = ChooseSide(p);
                Vector3 ball = p.Ball.Position;
                Vector3 strike = p.Position + StrikeZone.Offset(slot, side, ball.y, tuning);
                float distance = HorizontalDistance(strike, ball);
                bool heightOk = ball.y >= tuning.MinContactHeight && ball.y <= tuning.MaxContactHeight;
                bool inReach = heightOk && distance <= tuning.ReachRadius;

                float horizontalSpeed = HorizontalSpeed(p.Ball.Velocity);
                bool leaving = inReach && hasPreviousDistance && distance > previousDistance && horizontalSpeed >= MinHorizontalSpeedForLeaving;
                previousDistance = distance;
                hasPreviousDistance = true;

                if (inReach && (p.Now >= sweetTime || leaving))
                {
                    DisplaySide = side;
                    ShotResult shot = BuildShot(p, side, ball, strike, distance, sweetTime, velocity, aim, target);
                    EnterRecovery(p.Now);
                    return shot;
                }
            }

            if (p.Now > sweetTime + tuning.FollowThrough) EnterRecovery(p.Now);
            return null;
        }

        ShotResult BuildShot(in PlayerPerception p, HandSide side, Vector3 ball, Vector3 strike, float distance, float sweetTime, Vector3 velocity, Vector2 aim,
            Vector2? target)
        {
            float closestTime = ClosestApproachTime(p, strike);
            float moveRatio = velocity.magnitude / Mathf.Max(movementTuning.SprintSpeed, 0.1f);
            float spacing = SpacingAcrossBallPath(p, strike, distance);
            ContactEvaluation contact = ContactEvaluator.Evaluate(sweetTime - closestTime, spacing, moveRatio, tuning);

            var context = new ShotContext
            {
                ContactPoint = ball,
                IncomingVelocity = p.Ball.Velocity,
                Stretch = Mathf.Clamp01(spacing / tuning.ReachRadius),
                BallHasBounced = p.BouncesSinceHit > 0,
                Slot = slot,
                Button = ActiveButton,
                Side = side,
                Contact = contact,
                Aim = aim,
                Target = target,
                Preparation = preparationHeld / tuning.FullPreparationTime,
            };
            return ShotPlanner.Plan(context, tuning, physics, rng);
        }

        /// <summary>
        /// Footwork error only: how far the ball's line passes from the strike point. The error along
        /// the line is a timing error and is already graded as one, so it must not count twice.
        /// </summary>
        static float SpacingAcrossBallPath(in PlayerPerception p, Vector3 strike, float distance)
        {
            Vector3 horizontalVelocity = new Vector3(p.Ball.Velocity.x, 0f, p.Ball.Velocity.z);
            float speed = horizontalVelocity.magnitude;
            if (speed < MinSpeedForClosestApproach) return distance;

            Vector3 toStrike = strike - p.Ball.Position;
            toStrike.y = 0f;
            return Vector3.Cross(toStrike, horizontalVelocity / speed).magnitude;
        }

        float ClosestApproachTime(in PlayerPerception p, Vector3 strike)
        {
            Vector3 horizontalVelocity = new Vector3(p.Ball.Velocity.x, 0f, p.Ball.Velocity.z);
            float speedSquared = horizontalVelocity.sqrMagnitude;
            if (speedSquared < MinSpeedForClosestApproach * MinSpeedForClosestApproach)
                return Plan.Valid ? Plan.Time : p.Now; // A slow ball has no meaningful passing moment; judge against the plan.

            Vector3 toStrike = strike - p.Ball.Position;
            toStrike.y = 0f;
            return p.Now + Vector3.Dot(toStrike, horizontalVelocity) / speedSquared;
        }

        HandSide ChooseSide(in PlayerPerception p)
        {
            HandSide? forced = ForcedSide(ActiveButton);
            if (forced.HasValue) return forced.Value;
            if (Plan.Valid) return Plan.Side;
            float lateral = Vector3.Dot(p.Ball.Position - p.Position, StrikeZone.Right(slot));
            return lateral >= 0f ? HandSide.Forehand : HandSide.Backhand;
        }

        void EnterRecovery(float now)
        {
            Phase = SwingPhase.Recovering;
            releasedEarly = false;
            phaseEnd = now + tuning.RecoveryTime;
            Plan = default;
        }

        static HandSide? ForcedSide(ShotButton button)
        {
            if (button == ShotButton.Forehand) return HandSide.Forehand;
            if (button == ShotButton.Backhand) return HandSide.Backhand;
            return null;
        }

        static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static float HorizontalSpeed(Vector3 v) => Mathf.Sqrt(v.x * v.x + v.z * v.z);
    }
}
