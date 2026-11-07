using System;
using UnityEngine;
using Random = System.Random;

namespace TennisSim.Simulation
{
    public enum AiLevel { Easy, Medium, Hard }

    /// <summary>How good an AI opponent is. Every number is a human limitation it has more or less of.</summary>
    [Serializable]
    public class AiTuning
    {
        [Tooltip("Seconds from the opponent's hit until the AI starts preparing.")]
        public float ReactionTime;
        [Tooltip("Spread of the swing release around the ideal moment, seconds.")]
        public float TimingSpread;
        [Tooltip("Chance of simply holding the button through the ball: always playable, never best.")]
        public float HoldChance;
        [Tooltip("How close to the lines it dares aim, 0..1 of the widest aim.")]
        public float AimWidth;
        [Tooltip("Error when judging whether a ball will land out, metres.")]
        public float LineCallError;
        [Tooltip("Chance of following an attacking shot in to the net.")]
        public float ApproachChance;
        [Tooltip("Chance of a drop shot against an opponent far behind the baseline.")]
        public float DropShotChance;
        [Tooltip("Chance of a flat first serve; otherwise slice or kick.")]
        public float FirstServeRisk;
        public float ServeTimingSpread;
        [Tooltip("Runs flat out only when the ball is this far away, metres. Lower is fitter.")]
        public float SprintDistance;
        [Tooltip("Base chance of going for a winner from an ordinary ball; it grows as a rally drags on.")]
        public float Aggression;

        public static AiTuning For(AiLevel level)
        {
            switch (level)
            {
                case AiLevel.Easy:
                    return new AiTuning
                    {
                        ReactionTime = 0.3f, TimingSpread = 0.065f, HoldChance = 0.35f, AimWidth = 0.55f, LineCallError = 0.6f,
                        ApproachChance = 0.05f, DropShotChance = 0.05f, FirstServeRisk = 0.2f, ServeTimingSpread = 0.06f, SprintDistance = 4f,
                        Aggression = 0.08f,
                    };
                case AiLevel.Hard:
                    return new AiTuning
                    {
                        ReactionTime = 0.1f, TimingSpread = 0.02f, HoldChance = 0.03f, AimWidth = 0.92f, LineCallError = 0.08f,
                        ApproachChance = 0.45f, DropShotChance = 0.35f, FirstServeRisk = 0.55f, ServeTimingSpread = 0.022f, SprintDistance = 1f,
                        Aggression = 0.22f,
                    };
                default:
                    return new AiTuning
                    {
                        ReactionTime = 0.18f, TimingSpread = 0.04f, HoldChance = 0.15f, AimWidth = 0.78f, LineCallError = 0.25f,
                        ApproachChance = 0.2f, DropShotChance = 0.15f, FirstServeRisk = 0.4f, ServeTimingSpread = 0.038f, SprintDistance = 2.5f,
                        Aggression = 0.15f,
                    };
            }
        }
    }

    /// <summary>
    /// A match opponent. It plays only through intents, like a human with a gamepad: it moves with the stick,
    /// aims with the aim stick and presses and releases buttons, with a human's reaction time and timing errors.
    /// Between shots it covers the court; with the ball it picks a shot from where its opponent stands.
    /// </summary>
    public sealed class OpponentAI : IPlayerIntentSource
    {
        const float NetPosition = 3.4f;
        const float BaselineBehind = 0.6f;
        const float CoverFactor = 0.45f;
        const float MaxCoverX = 2.6f;
        const float ArrivedDistance = 0.25f;
        const float AtNetDepth = 6f;
        const float DeepBehindBaseline = 2.5f;
        const float AttackHeight = 1.2f;

        readonly PlayerAgent self;
        readonly Random rng;

        AiTuning tuning;
        ShotButton? held;
        Vector2 aim;
        bool holdThrough;
        float releaseOffset;
        int seenRevision = -1;
        float revisionSeenAt;
        int decidedRevision = -1;
        float lineCallError;
        bool atNet;
        int shotsThisPoint;
        float serveWaitUntil = -1f;

        public AiLevel Level { get; private set; }

        public OpponentAI(PlayerAgent self, AiLevel level, Random rng)
        {
            this.self = self;
            this.rng = rng;
            SetLevel(level);
        }

        public void SetLevel(AiLevel level)
        {
            Level = level;
            tuning = AiTuning.For(level);
        }

        public PlayerIntent ReadIntent(in PlayerPerception p)
        {
            var intent = new PlayerIntent { Aim = aim };

            if (p.IsServer && (p.AwaitingServe || p.Tossing))
            {
                atNet = false;
                shotsThisPoint = 0;
                Serve(p, ref intent);
                return intent;
            }
            serveWaitUntil = -1f;

            if (held.HasValue && (!p.CanHit || p.AwaitingServe))
            {
                intent.Released = held;
                held = null;
                return intent;
            }
            if (p.AwaitingServe)
            {
                atNet = false;
                shotsThisPoint = 0;
                return intent;
            }

            if (p.BallRevision != seenRevision)
            {
                seenRevision = p.BallRevision;
                revisionSeenAt = p.Now;
                lineCallError = ShotPlanner.Gaussian(rng) * tuning.LineCallError;
                if (p.CanHit) LeaveNetIfLobbed(p);
            }

            if (p.CanHit)
            {
                PlayTheBall(p, ref intent);
                return intent;
            }

            MoveTo(p, CoverPosition(p), ref intent);
            return intent;
        }

        // ---------------------------------------------------------------- With the ball

        void PlayTheBall(in PlayerPerception p, ref PlayerIntent intent)
        {
            bool reacted = p.Now - revisionSeenAt >= tuning.ReactionTime;
            if (!held.HasValue && reacted && decidedRevision != p.BallRevision)
            {
                decidedRevision = p.BallRevision;
                if (!WillLandOut(p))
                {
                    (ShotButton button, Vector2 shotAim) = ChooseShot(p);
                    shotsThisPoint++;
                    aim = shotAim;
                    intent.Aim = aim;
                    intent.Pressed = button;
                    intent.Sprint = true;
                    held = button;
                    holdThrough = rng.NextDouble() < tuning.HoldChance;
                    releaseOffset = ShotPlanner.Gaussian(rng) * tuning.TimingSpread;
                    return;
                }
            }

            if (held.HasValue)
            {
                // The stick stays neutral: the player runs to the contact point by themselves, and the aim stick aims.
                intent.Sprint = true;
                ContactPlan plan = self.Shot.Plan;
                if (!holdThrough && plan.Valid && p.Now >= plan.Time - self.ShotTuning.SwingLatency + releaseOffset)
                {
                    intent.Released = held;
                    held = null;
                }
                return;
            }

            // A ball it lets go, or one it has not reacted to yet: keep the court covered.
            MoveTo(p, CoverPosition(p), ref intent);
        }

        bool WillLandOut(in PlayerPerception p)
        {
            if (p.BouncesSinceHit > 0 || p.Trajectory == null || !p.Trajectory.HasFirstBounce) return false;
            Vector3 landing = p.Trajectory.FirstBouncePoint;
            float x = landing.x + lineCallError;
            float z = landing.z + lineCallError * CourtGeometry.ForwardSign(p.Slot);
            if (p.MustLetBounce) return !p.ServeBox.Contains(x, z);
            // A ball that will land on this side can be volleyed out of trouble, but a clear out is left alone.
            return CourtGeometry.HalfOwner(landing.z) == p.Slot && !CourtGeometry.IsInsideSingles(x, z);
        }

        (ShotButton, Vector2) ChooseShot(in PlayerPerception p)
        {
            ShotTuning shots = self.ShotTuning;
            float forward = CourtGeometry.ForwardSign(p.Slot);
            ContactPlan plan = ContactPlanner.Find(p.Trajectory, p.Position, p.Slot, null, ShotButton.Auto, p.Now,
                self.Movement.Tuning.SprintSpeed, self.Movement.Tuning, shots, p.MustLetBounce);

            Vector3 opponent = p.OpponentPosition;
            float opponentDepth = opponent.z * forward;
            float myDepth = -p.Position.z * forward;
            float open = opponent.x > 0f ? -1f : 1f;
            float width = tuning.AimWidth;
            Vector3 contact = plan.Valid ? plan.BallPoint : p.Ball.Position;

            // Overhead: put it away into the open court.
            if (plan.Valid && !plan.BallHasBounced && contact.y >= shots.SmashMinHeight)
                return (ShotButton.Auto, Aim(open * width, 0.4f, forward));

            // At full stretch only a defensive shot comes back: high and deep, or lobbed over a net player.
            if (!plan.Valid || !plan.Reachable)
                return opponentDepth < AtNetDepth
                    ? (ShotButton.Lob, Aim(0f, 0.5f, forward))
                    : (ShotButton.Slice, Aim(0f, 0.7f, forward));

            // The opponent is at the net: lob over them or pass them.
            if (opponentDepth < AtNetDepth)
            {
                if (rng.NextDouble() < 0.4) return (ShotButton.Lob, Aim(open * width * 0.5f, 0.6f, forward));
                return (ShotButton.Flat, Aim(open * width, -0.3f, forward));
            }

            // At the net: angle the volley away.
            if (!plan.BallHasBounced)
                return (ShotButton.Volley, Aim(open * width, 0.2f, forward));

            // The opponent is camped deep: bring them in. Far behind the baseline it is an obvious play, on it an occasional surprise.
            float stanceDepth = -plan.Stance.z * forward;
            bool insideCourt = stanceDepth < CourtGeometry.HalfLength;
            double dropChance = opponentDepth > CourtGeometry.HalfLength + DeepBehindBaseline ? tuning.DropShotChance
                : opponentDepth > CourtGeometry.HalfLength ? tuning.DropShotChance * 0.25 : 0.0;
            if (insideCourt && rng.NextDouble() < dropChance)
                return (ShotButton.Drop, Aim(open * width * 0.6f, -0.5f, forward));

            // A short or high ball is a chance to attack; and the longer the rally, the more likely it goes for it anyway.
            bool shortBall = stanceDepth < CourtGeometry.HalfLength - 1.5f;
            bool highBall = contact.y > AttackHeight;
            float impatience = tuning.Aggression * (1f + 0.15f * Mathf.Max(0, shotsThisPoint - 4));
            if (shortBall || (highBall && rng.NextDouble() < impatience * 2.5) || rng.NextDouble() < impatience)
            {
                if (rng.NextDouble() < (shortBall ? tuning.ApproachChance * 2f : tuning.ApproachChance * 0.5f)) atNet = true;
                ShotButton attack = rng.NextDouble() < 0.6 ? ShotButton.Flat : ShotButton.Auto;
                return (attack, Aim(open * Mathf.Min(1f, width * 1.1f), 0.5f + 0.4f * (float)rng.NextDouble(), forward));
            }

            // Now and then a high, deep moonball to break the rhythm.
            if (rng.NextDouble() < 0.03)
                return (ShotButton.Lob, Aim(open * width * 0.5f, 0.6f, forward));

            // Rally: mostly topspin, cross-court or into the open court, sometimes a slice to change the pace.
            ShotButton rally = rng.NextDouble() < 0.18 ? ShotButton.Slice : ShotButton.Auto;
            float crossCourt = contact.x > 0f ? -1f : 1f;
            float side = rng.NextDouble() < 0.5 ? open : crossCourt;
            float depth = 0.2f + 0.6f * (float)rng.NextDouble();
            return (rally, Aim(side * width * (0.7f + 0.3f * (float)rng.NextDouble()), depth, forward));
        }

        /// <summary>Aim in world space: across is -1..1 along world X, depth is -1 (short) .. 1 (deep) towards the opponent.</summary>
        static Vector2 Aim(float across, float depth, float forward) => new Vector2(across, depth * forward);

        // ---------------------------------------------------------------- Without the ball

        /// <summary>
        /// Between shots: in the middle of the opponent's possible replies, which shifts towards the side the
        /// ball is going to, either behind the baseline or at the net after an approach.
        /// </summary>
        Vector3 CoverPosition(in PlayerPerception p)
        {
            float forward = CourtGeometry.ForwardSign(p.Slot);
            float depth = atNet ? NetPosition : CourtGeometry.HalfLength + BaselineBehind;
            float x = 0f;
            if (p.Trajectory != null && p.Trajectory.HasFirstBounce && CourtGeometry.HalfOwner(p.Trajectory.FirstBouncePoint.z) != p.Slot)
                x = Mathf.Clamp(p.Trajectory.FirstBouncePoint.x * CoverFactor, -MaxCoverX, MaxCoverX);
            return new Vector3(x, 0f, -depth * forward);
        }

        /// <summary>A lob over a player at the net sends them back to the baseline.</summary>
        void LeaveNetIfLobbed(in PlayerPerception p)
        {
            if (!atNet || p.Trajectory == null || !p.Trajectory.HasFirstBounce) return;
            float landingDepth = -p.Trajectory.FirstBouncePoint.z * CourtGeometry.ForwardSign(p.Slot);
            float apex = 0f;
            foreach (TrajectorySample sample in p.Trajectory.Samples)
            {
                if (sample.Bounces > 0) break;
                apex = Mathf.Max(apex, sample.State.Position.y);
            }
            if (apex > 4f && landingDepth > AtNetDepth) atNet = false;
        }

        void MoveTo(in PlayerPerception p, Vector3 target, ref PlayerIntent intent)
        {
            Vector3 offset = target - p.Position;
            offset.y = 0f;
            float distance = offset.magnitude;
            if (distance < ArrivedDistance) return;
            float speed = Mathf.Clamp(distance / 1.5f, 0.3f, 1f);
            intent.Move = new Vector2(offset.x, offset.z) / distance * speed;
            intent.Sprint = distance > tuning.SprintDistance;
        }

        // ---------------------------------------------------------------- Serve

        void Serve(in PlayerPerception p, ref PlayerIntent intent)
        {
            if (p.AwaitingServe)
            {
                if (held.HasValue)
                {
                    intent.Released = held;
                    held = null;
                    return;
                }
                if (serveWaitUntil < 0f) serveWaitUntil = p.Now + 0.8f + 0.6f * (float)rng.NextDouble();
                if (p.Now < serveWaitUntil) return;
                serveWaitUntil = -1f;

                ShotButton button = ChooseServe(p.ServeNumber);
                // Wide, body or down the T. The box's sides are world X, so aim across it directly.
                double roll = rng.NextDouble();
                float across = roll < 0.4 ? -0.85f : roll < 0.8 ? 0.85f : 0f;
                float depth = button == ShotButton.Flat ? 0.5f : 0.2f;
                aim = new Vector2(across * Mathf.Lerp(0.6f, 1f, tuning.AimWidth), depth * CourtGeometry.ForwardSign(p.Slot));
                intent.Aim = aim;
                intent.Pressed = button;
                held = button;
                releaseOffset = ShotPlanner.Gaussian(rng) * tuning.ServeTimingSpread;
                return;
            }

            if (held.HasValue && !float.IsNaN(p.ServeContactTime) && p.Now >= p.ServeContactTime - self.ShotTuning.SwingLatency + releaseOffset)
            {
                intent.Released = held;
                held = null;
            }
        }

        ShotButton ChooseServe(int serveNumber)
        {
            if (serveNumber >= 2) return Level == AiLevel.Hard && rng.NextDouble() < 0.3 ? ShotButton.Slice : ShotButton.Auto;
            double roll = rng.NextDouble();
            if (roll < tuning.FirstServeRisk) return ShotButton.Flat;
            return roll < tuning.FirstServeRisk + (1.0 - tuning.FirstServeRisk) * 0.5 ? ShotButton.Slice : ShotButton.Auto;
        }
    }
}
