using System;
using UnityEngine;
using Random = System.Random;

namespace TennisSim.Simulation
{
    public struct ContactEvaluation
    {
        public float Score;
        public ContactQuality Quality;
        /// <summary>Seconds the racket was early (negative) or late (positive) against the ball.</summary>
        public float TimingError;
    }

    public struct ShotContext
    {
        public Vector3 ContactPoint;
        /// <summary>The ball's velocity as the racket meets it.</summary>
        public Vector3 IncomingVelocity;
        public bool BallHasBounced;
        public PlayerSlot Slot;
        public ShotButton Button;
        public HandSide Side;
        public ContactEvaluation Contact;
        /// <summary>World-space aim: X across the court, Y along +Z. Length up to 1.</summary>
        public Vector2 Aim;
        /// <summary>0..1, how long the player prepared relative to a full preparation.</summary>
        public float Preparation;
        /// <summary>How far out of the ideal strike point the ball was met, as a fraction of the reach, 0..1.</summary>
        public float Stretch;
    }

    public struct ShotResult
    {
        public ShotKind Kind;
        public HandSide Side;
        public ContactQuality Quality;
        public float Score;
        /// <summary>Seconds early (negative) or late (positive).</summary>
        public float TimingError;
        public Vector3 Velocity;
        public Vector3 Spin;
        public Vector2 AimedTarget;
        public bool Solved;
    }

    public static class ContactEvaluator
    {
        /// <param name="timingError">Seconds between the racket's sweet spot and the ball's closest approach.</param>
        /// <param name="spacing">Metres between the ideal strike point and the ball at contact.</param>
        /// <param name="moveRatio">Player speed relative to sprint speed, 0..1.</param>
        public static ContactEvaluation Evaluate(float timingError, float spacing, float moveRatio, ShotTuning t) =>
            Evaluate(timingError, spacing, moveRatio, t.TimingWindow, t);

        public static ContactEvaluation Evaluate(float timingError, float spacing, float moveRatio, float timingWindow, ShotTuning t)
        {
            float timing = 1f - Mathf.Clamp01(Mathf.Abs(timingError) / timingWindow);
            float position = 1f - Mathf.Clamp01(spacing / t.ReachRadius);
            float score = Mathf.Clamp01(t.TimingWeight * timing + t.SpacingWeight * position - t.MovingPenalty * Mathf.Clamp01(moveRatio));
            ContactQuality quality = score >= t.PerfectScore ? ContactQuality.Perfect
                : score >= t.GoodScore ? ContactQuality.Good
                : ContactQuality.Poor;
            return new ContactEvaluation { Score = score, Quality = quality, TimingError = timingError };
        }
    }

    /// <summary>Turns "what the player asked for and how well they met the ball" into launch conditions.</summary>
    public static class ShotPlanner
    {
        const float NoPaceBelow = 20f;
        const float MaxSpeedOverProfile = 1.15f;
        const float AimDeadZone = 0.1f;

        /// <summary>What actually happens depends on the ball: an airborne ball is a volley, a high one a smash.</summary>
        public static ShotKind ResolveKind(ShotButton button, float contactHeight, bool ballHasBounced, ShotTuning t) =>
            ResolveKind(button, contactHeight, ballHasBounced, false, 0f, t);

        /// <param name="rising">The ball is still going up from its bounce.</param>
        /// <param name="stretch">How far out of reach the ball was met, 0..1 of the reach radius.</param>
        public static ShotKind ResolveKind(ShotButton button, float contactHeight, bool ballHasBounced, bool rising, float stretch, ShotTuning t)
        {
            if (button == ShotButton.Lob) return ShotKind.Lob;
            if (button == ShotButton.Drop) return ShotKind.Drop;
            if (contactHeight >= t.SmashMinHeight) return ShotKind.Smash;
            if (!ballHasBounced) return ShotKind.Volley;
            if (stretch >= t.DefensiveReach) return ShotKind.Defensive;
            if (rising && contactHeight <= t.HalfVolleyMaxHeight) return ShotKind.HalfVolley;
            if (button == ShotButton.Slice) return ShotKind.Slice;
            if (button == ShotButton.Flat) return ShotKind.Flat;
            return ShotKind.Topspin;
        }

        public static ShotResult Plan(in ShotContext c, ShotTuning t, in BallPhysicsParams physics, Random rng)
        {
            ShotKind kind = ResolveKind(c.Button, c.ContactPoint.y, c.BallHasBounced, c.IncomingVelocity.y > 0f, c.Stretch, t);
            ShotProfile profile = t.Profile(kind);
            float forward = CourtGeometry.ForwardSign(c.Slot);

            Vector2 aimed = AimPoint(c, profile, forward);
            Vector2 pulled = aimed + new Vector2(MistimePull(c, t), 0f);

            float power = 0.4f * Mathf.Clamp01(c.Preparation) + 0.6f * c.Contact.Score;
            float speed = Mathf.Lerp(profile.MinSpeed, profile.MaxSpeed, power) * PaceFactor(kind, c, t)
                        + AbsorbedPace(kind, c, t);
            speed = Mathf.Min(speed, profile.MaxSpeed * MaxSpeedOverProfile);

            ShotResult result = Launch(kind, c.ContactPoint, pulled, speed, profile, c.Contact, forward, t, physics, rng);
            result.Side = c.Side;
            result.AimedTarget = aimed;
            return result;
        }

        /// <summary>A serve struck at the top of the toss, aimed inside the service box.</summary>
        public static ShotResult PlanServe(ShotKind kind, PlayerSlot slot, Vector3 contact, ServiceBox box, Vector2 aim,
            ContactEvaluation evaluation, ShotTuning t, in BallPhysicsParams physics, Random rng)
        {
            ShotProfile profile = t.Profile(kind);
            float forward = CourtGeometry.ForwardSign(slot);

            float across = Mathf.Abs(aim.x) > AimDeadZone ? Mathf.Clamp(aim.x, -1f, 1f) : 0f;
            float x = Mathf.Lerp(box.MinX + t.ServeLineMargin, box.MaxX - t.ServeLineMargin, (across + 1f) * 0.5f);
            float depthInput = Mathf.Abs(aim.y) > AimDeadZone ? Mathf.Clamp(aim.y * forward, -1f, 1f) : 0f;
            float depth = depthInput >= 0f
                ? Mathf.Lerp(profile.DefaultDepth, profile.MaxDepth, depthInput)
                : Mathf.Lerp(profile.DefaultDepth, profile.MinDepth, -depthInput);
            var aimed = new Vector2(x, depth * forward);

            float speed = Mathf.Lerp(profile.MinSpeed, profile.MaxSpeed, evaluation.Score);
            ShotResult result = Launch(kind, contact, aimed, speed, profile, evaluation, forward, t, physics, rng);
            result.Side = HandSide.Forehand;
            result.AimedTarget = aimed;
            return result;
        }

        static ShotResult Launch(ShotKind kind, Vector3 contact, Vector2 aimed, float speed, ShotProfile profile, ContactEvaluation contactEval,
            float forward, ShotTuning t, in BallPhysicsParams physics, Random rng)
        {
            QualityError error = t.Error(contactEval.Quality);
            Vector2 target = aimed + new Vector2(
                Gaussian(rng) * error.LateralSigma * profile.ErrorScale,
                Gaussian(rng) * error.DepthSigma * profile.ErrorScale * forward);

            float spinScale = 0.6f + 0.4f * contactEval.Score;
            ShotSolution solution = ShotSolver.Solve(contact, target, speed, profile.Spin * spinScale, profile.NetClearance,
                profile.LaunchElevation, physics, profile.SideSpin * spinScale);
            Vector3 velocity = PerturbElevation(solution.Velocity, Gaussian(rng) * error.ElevationSigma * profile.ErrorScale);

            return new ShotResult
            {
                Kind = kind,
                Quality = contactEval.Quality,
                Score = contactEval.Score,
                TimingError = contactEval.TimingError,
                Velocity = velocity,
                Spin = solution.Spin,
                Solved = solution.Solved,
            };
        }

        static Vector2 AimPoint(in ShotContext c, ShotProfile profile, float forward)
        {
            // With no aim input the ball drifts back across the court instead of always going down the middle.
            float x = Mathf.Abs(c.Aim.x) > AimDeadZone
                ? Mathf.Clamp(c.Aim.x, -1f, 1f) * profile.MaxAimX
                : Mathf.Clamp(-c.ContactPoint.x * 0.25f, -profile.MaxAimX, profile.MaxAimX);

            float depthInput = Mathf.Clamp(c.Aim.y * forward, -1f, 1f);
            float depth = depthInput >= 0f
                ? Mathf.Lerp(profile.DefaultDepth, profile.MaxDepth, depthInput)
                : Mathf.Lerp(profile.DefaultDepth, profile.MinDepth, -depthInput);

            return new Vector2(x, depth * forward);
        }

        /// <summary>
        /// Meeting the ball early swings it across the body; late pushes it the other way. For a right-hander's
        /// forehand early means to their left, for a backhand to their right. Drop shots and lobs are touch shots and are not pulled.
        /// </summary>
        static float MistimePull(in ShotContext c, ShotTuning t)
        {
            if (c.Button == ShotButton.Lob || c.Button == ShotButton.Drop) return 0f;
            float early = Mathf.Clamp(-c.Contact.TimingError / t.TimingWindow, -1f, 1f);
            float hitterRight = CourtGeometry.ForwardSign(c.Slot);
            float across = c.Side == HandSide.Forehand ? -hitterRight : hitterRight;
            return early * t.MistimePull * across;
        }

        /// <summary>A high bounce can be hit down hard. Low balls have to be lifted and are slower.</summary>
        static float PaceFactor(ShotKind kind, in ShotContext c, ShotTuning t)
        {
            if (kind != ShotKind.Topspin && kind != ShotKind.Flat) return 1f;
            float height = c.ContactPoint.y;
            if (height > t.AttackHeight) return 1f + t.AttackBonus * Mathf.Clamp01((height - t.AttackHeight) / 0.6f);
            if (height < 0.6f) return Mathf.Lerp(0.88f, 1f, height / 0.6f);
            return 1f;
        }

        /// <summary>Blocked and flat shots reuse the opponent's pace.</summary>
        static float AbsorbedPace(ShotKind kind, in ShotContext c, ShotTuning t)
        {
            if (kind != ShotKind.Flat && kind != ShotKind.Volley && kind != ShotKind.HalfVolley) return 0f;
            return Mathf.Max(0f, c.IncomingVelocity.magnitude - NoPaceBelow) * t.PaceAbsorption;
        }

        static Vector3 PerturbElevation(Vector3 velocity, float degrees)
        {
            Vector3 axis = Vector3.Cross(velocity, Vector3.up);
            if (axis.sqrMagnitude < 1e-6f) return velocity;
            axis = axis.normalized;
            // The axis is perpendicular to the velocity, so Rodrigues' rotation reduces to two terms.
            float radians = degrees * Mathf.Deg2Rad;
            return velocity * Mathf.Cos(radians) + Vector3.Cross(axis, velocity) * Mathf.Sin(radians);
        }

        internal static float Gaussian(Random rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
    }
}
