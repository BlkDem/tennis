using System;
using UnityEngine;
using Random = System.Random;

namespace TennisSim.Simulation
{
    public struct ContactEvaluation
    {
        public float Score;
        public ContactQuality Quality;
    }

    public struct ShotContext
    {
        public Vector3 ContactPoint;
        public bool BallHasBounced;
        public PlayerSlot Slot;
        public ShotButton Button;
        public HandSide Side;
        public ContactEvaluation Contact;
        /// <summary>World-space aim: X across the court, Y along +Z. Length up to 1.</summary>
        public Vector2 Aim;
        /// <summary>0..1, how long the player prepared relative to a full preparation.</summary>
        public float Preparation;
    }

    public struct ShotResult
    {
        public ShotKind Kind;
        public HandSide Side;
        public ContactQuality Quality;
        public float Score;
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
        public static ContactEvaluation Evaluate(float timingError, float spacing, float moveRatio, ShotTuning t)
        {
            float timing = 1f - Mathf.Clamp01(Mathf.Abs(timingError) / t.TimingWindow);
            float position = 1f - Mathf.Clamp01(spacing / t.ReachRadius);
            float score = Mathf.Clamp01(t.TimingWeight * timing + t.SpacingWeight * position - t.MovingPenalty * Mathf.Clamp01(moveRatio));
            ContactQuality quality = score >= t.PerfectScore ? ContactQuality.Perfect
                : score >= t.GoodScore ? ContactQuality.Good
                : ContactQuality.Poor;
            return new ContactEvaluation { Score = score, Quality = quality };
        }
    }

    /// <summary>Turns "what the player asked for and how well they met the ball" into launch conditions.</summary>
    public static class ShotPlanner
    {
        /// <summary>What actually happens depends on the ball: an airborne ball is a volley, a high one a smash.</summary>
        public static ShotKind ResolveKind(ShotButton button, float contactHeight, bool ballHasBounced, ShotTuning t)
        {
            if (button == ShotButton.Lob) return ShotKind.Lob;
            if (button == ShotButton.Drop) return ShotKind.Drop;
            if (contactHeight >= t.SmashMinHeight) return ShotKind.Smash;
            if (!ballHasBounced) return ShotKind.Volley;
            if (button == ShotButton.Slice) return ShotKind.Slice;
            if (button == ShotButton.Flat) return ShotKind.Flat;
            return ShotKind.Topspin;
        }

        public static ShotResult Plan(in ShotContext c, ShotTuning t, in BallPhysicsParams physics, Random rng)
        {
            ShotKind kind = ResolveKind(c.Button, c.ContactPoint.y, c.BallHasBounced, t);
            ShotProfile profile = t.Profile(kind);
            QualityError error = t.Error(c.Contact.Quality);
            float forward = CourtGeometry.ForwardSign(c.Slot);

            Vector2 aimed = AimPoint(c, profile, forward);
            Vector2 target = aimed + new Vector2(
                Gaussian(rng) * error.LateralSigma * profile.ErrorScale,
                Gaussian(rng) * error.DepthSigma * profile.ErrorScale * forward);

            float power = 0.4f * Mathf.Clamp01(c.Preparation) + 0.6f * c.Contact.Score;
            float speed = Mathf.Lerp(profile.MinSpeed, profile.MaxSpeed, power);
            float spinRate = profile.Spin * (0.6f + 0.4f * c.Contact.Score);

            ShotSolution solution = ShotSolver.Solve(c.ContactPoint, target, speed, spinRate, profile.NetClearance, profile.LaunchElevation, physics);
            Vector3 velocity = PerturbElevation(solution.Velocity, Gaussian(rng) * error.ElevationSigma * profile.ErrorScale);

            return new ShotResult
            {
                Kind = kind,
                Side = c.Side,
                Quality = c.Contact.Quality,
                Score = c.Contact.Score,
                Velocity = velocity,
                Spin = solution.Spin,
                AimedTarget = aimed,
                Solved = solution.Solved,
            };
        }

        static Vector2 AimPoint(in ShotContext c, ShotProfile profile, float forward)
        {
            // With no aim input the ball drifts back across the court instead of always going down the middle.
            float x = Mathf.Abs(c.Aim.x) > 0.1f
                ? Mathf.Clamp(c.Aim.x, -1f, 1f) * profile.MaxAimX
                : Mathf.Clamp(-c.ContactPoint.x * 0.25f, -profile.MaxAimX, profile.MaxAimX);

            float depthInput = Mathf.Clamp(c.Aim.y * forward, -1f, 1f);
            float depth = depthInput >= 0f
                ? Mathf.Lerp(profile.DefaultDepth, profile.MaxDepth, depthInput)
                : Mathf.Lerp(profile.DefaultDepth, profile.MinDepth, -depthInput);

            return new Vector2(x, depth * forward);
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

        static float Gaussian(Random rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
    }
}
