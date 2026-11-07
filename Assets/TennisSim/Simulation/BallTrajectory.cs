using System.Collections.Generic;
using UnityEngine;

namespace TennisSim.Simulation
{
    public struct TrajectorySample
    {
        public float Time;
        public BallState State;
        /// <summary>Bounces since the last racket contact, including this moment.</summary>
        public int Bounces;
    }

    /// <summary>The ball's future, computed with the same physics as the live ball and therefore exact until the next hit.</summary>
    public sealed class BallTrajectory
    {
        const int MaxBounces = 3;

        public readonly List<TrajectorySample> Samples = new List<TrajectorySample>(512);

        public bool HasFirstBounce { get; private set; }
        public Vector3 FirstBouncePoint { get; private set; }

        public void Clear()
        {
            Samples.Clear();
            HasFirstBounce = false;
        }

        public void Compute(in BallState from, float startTime, in BallPhysicsParams p, float dt, float horizon)
        {
            Clear();
            BallState state = from;
            int bounces = 0;
            int steps = Mathf.CeilToInt(horizon / dt);
            for (int i = 1; i <= steps; i++)
            {
                BallContact contact = BallSimulator.Step(ref state, p, dt);
                if (contact.Type == BallContactType.Ground)
                {
                    if (bounces == 0)
                    {
                        HasFirstBounce = true;
                        FirstBouncePoint = contact.Point;
                    }
                    bounces++;
                    if (bounces >= MaxBounces) break;
                }
                Samples.Add(new TrajectorySample { Time = startTime + i * dt, State = state, Bounces = bounces });
            }
        }
    }
}
