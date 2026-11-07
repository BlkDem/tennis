using System;
using UnityEngine;

namespace TennisSim.Simulation
{
    public enum ShotKind { Topspin, Slice, Flat, Lob, Drop, Volley, Smash }
    public enum HandSide { Forehand, Backhand }
    public enum ContactQuality { Perfect, Good, Poor }

    /// <summary>What the player asked for. The contact itself decides the final <see cref="ShotKind"/>.</summary>
    public enum ShotButton { Auto, Forehand, Backhand, Slice, Flat, Volley, Lob, Drop }

    [Serializable]
    public class ShotProfile
    {
        public ShotKind Kind;
        [Tooltip("Ball speed at low power, m/s. Ignored when Launch Elevation is set.")]
        public float MinSpeed;
        [Tooltip("Ball speed at full power, m/s. Ignored when Launch Elevation is set.")]
        public float MaxSpeed;
        [Tooltip("Spin in rad/s: positive is topspin, negative is backspin.")]
        public float Spin;
        [Tooltip("Height the ball must clear the net by, in metres.")]
        public float NetClearance;
        [Tooltip("If above zero, the shot is flown at this launch angle and the solver finds the speed (lobs, drop shots).")]
        public float LaunchElevation;
        [Tooltip("Default distance of the landing point from the net.")]
        public float DefaultDepth;
        public float MinDepth;
        public float MaxDepth;
        [Tooltip("Furthest landing point from the centre line when aiming hard left or right.")]
        public float MaxAimX;
        [Tooltip("Multiplies the aim error of this shot. Below 1 is more precise, above 1 is riskier.")]
        public float ErrorScale;

        public ShotProfile() { }

        public ShotProfile(ShotKind kind, float minSpeed, float maxSpeed, float spin, float clearance, float elevation,
            float defaultDepth, float minDepth, float maxDepth, float maxAimX, float errorScale)
        {
            Kind = kind;
            MinSpeed = minSpeed;
            MaxSpeed = maxSpeed;
            Spin = spin;
            NetClearance = clearance;
            LaunchElevation = elevation;
            DefaultDepth = defaultDepth;
            MinDepth = minDepth;
            MaxDepth = maxDepth;
            MaxAimX = maxAimX;
            ErrorScale = errorScale;
        }
    }

    [Serializable]
    public struct QualityError
    {
        [Tooltip("Standard deviation of the aim point across the court, metres.")]
        public float LateralSigma;
        [Tooltip("Standard deviation of the aim point along the court, metres.")]
        public float DepthSigma;
        [Tooltip("Standard deviation of the launch angle, degrees.")]
        public float ElevationSigma;
    }

    /// <summary>All balance values of the shot system. Wrapped by ShotConfig so designers can edit it as an asset.</summary>
    [Serializable]
    public class ShotTuning
    {
        [Header("Swing timing, seconds")]
        [Tooltip("Time from the start of the swing to the moment the racket is at its sweet spot.")]
        public float SwingLatency = 0.14f;
        [Tooltip("How long after the sweet spot the racket can still reach the ball.")]
        public float FollowThrough = 0.12f;
        [Tooltip("A held button auto-swings this late relative to the ideal moment, so waiting never beats good timing.")]
        public float AutoSwingLateness = 0.075f;
        public float RecoveryTime = 0.16f;
        [Tooltip("Preparation time that gives full power.")]
        public float FullPreparationTime = 0.7f;

        [Header("Reach, metres")]
        public float StrikeLateral = 0.85f;
        public float StrikeForward = 0.35f;
        public float OverheadLateral = 0.25f;
        public float OverheadForward = 0.2f;
        public float ReachRadius = 0.95f;
        public float MinContactHeight = 0.15f;
        public float MaxContactHeight = 3.1f;
        public float IdealContactHeight = 1.0f;
        [Tooltip("A ball taken in the air above this height becomes a smash.")]
        public float SmashMinHeight = 2.2f;

        [Header("Contact quality")]
        [Tooltip("Timing error at which the timing score reaches zero, seconds.")]
        public float TimingWindow = 0.2f;
        public float TimingWeight = 0.7f;
        public float SpacingWeight = 0.3f;
        [Tooltip("Score lost when hitting at full sprint speed.")]
        public float MovingPenalty = 0.25f;
        public float PerfectScore = 0.8f;
        public float GoodScore = 0.6f;

        [Header("Errors by contact quality")]
        public QualityError PerfectError = new QualityError { LateralSigma = 0.2f, DepthSigma = 0.3f, ElevationSigma = 0.1f };
        public QualityError GoodError = new QualityError { LateralSigma = 0.85f, DepthSigma = 1.15f, ElevationSigma = 0.6f };
        public QualityError PoorError = new QualityError { LateralSigma = 1.5f, DepthSigma = 2.2f, ElevationSigma = 1.8f };

        [Header("Shots")]
        public ShotProfile[] Profiles = DefaultProfiles();

        public ShotProfile Profile(ShotKind kind)
        {
            foreach (ShotProfile profile in Profiles)
                if (profile.Kind == kind) return profile;
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "No profile configured for this shot.");
        }

        public QualityError Error(ContactQuality quality)
        {
            switch (quality)
            {
                case ContactQuality.Perfect: return PerfectError;
                case ContactQuality.Good: return GoodError;
                default: return PoorError;
            }
        }

        public static ShotProfile[] DefaultProfiles() => new[]
        {
            //              kind             min   max   spin  clear elev  depth  min   max   aimX err
            new ShotProfile(ShotKind.Topspin, 21f, 33f,  300f, 0.55f, 0f,  8.9f, 5.5f, 10.6f, 3.4f, 1f),
            new ShotProfile(ShotKind.Slice,   18f, 26f, -190f, 0.30f, 0f,  8.3f, 5f,   10.4f, 3.4f, 0.9f),
            new ShotProfile(ShotKind.Flat,    28f, 42f,   40f, 0.20f, 0f,  9.2f, 6f,   10.7f, 3.5f, 1.45f),
            new ShotProfile(ShotKind.Lob,     12f, 24f,  190f, 2.2f, 56f, 10f,   7.5f, 11f,   3.1f, 1.1f),
            new ShotProfile(ShotKind.Drop,     6f, 20f, -240f, 0.25f, 24f, 2.6f, 1.6f, 4.2f,  3.0f, 1.25f),
            new ShotProfile(ShotKind.Volley,  17f, 28f,  -70f, 0.12f, 0f,  8f,   4f,   10.2f, 3.6f, 0.85f),
            new ShotProfile(ShotKind.Smash,   32f, 46f,   30f, 0.15f, 0f,  8.5f, 5f,   10.6f, 3.6f, 1.1f),
        };
    }
}
