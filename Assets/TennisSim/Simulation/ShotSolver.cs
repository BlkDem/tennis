using UnityEngine;

namespace TennisSim.Simulation
{
    public struct ShotSolution
    {
        public bool Solved;
        public Vector3 Velocity;
        public Vector3 Spin;
        public float NetClearance;
    }

    /// <summary>
    /// Finds the launch velocity that lands the ball on a target under the real ball physics.
    /// The shot still flies as free physics afterwards: this only picks the initial conditions.
    /// </summary>
    public static class ShotSolver
    {
        const float SolverDt = 1f / 120f;
        const float FlightLimit = 6f;
        const float ScanStepDegrees = 4f;
        const float MinElevationDegrees = -26f;
        const float MaxElevationDegrees = 66f;
        const int BisectionSteps = 12;
        const int SpeedAttempts = 16;
        const float SpeedBackoff = 0.92f;
        const float MinSpeedFraction = 0.25f;
        const int ElevationAttempts = 6;
        const float ElevationBackoffDegrees = 6f;

        /// <summary>Axis of topspin for a ball moving in this horizontal direction.</summary>
        public static Vector3 TopspinAxis(Vector3 horizontalDirection) => Vector3.Cross(Vector3.up, horizontalDirection);

        public static ShotSolution Solve(Vector3 contact, Vector2 targetXZ, float speed, float spinRate, float clearance,
            float fixedElevationDegrees, in BallPhysicsParams p)
        {
            Vector3 toTarget = new Vector3(targetXZ.x - contact.x, 0f, targetXZ.y - contact.z);
            float distance = toTarget.magnitude;
            if (distance < 0.01f) return default;

            Vector3 direction = toTarget / distance;
            Vector3 spin = TopspinAxis(direction) * spinRate;

            return fixedElevationDegrees > 0f
                ? SolveArc(contact, direction, spin, distance, clearance, fixedElevationDegrees, p)
                : SolveDriven(contact, direction, spin, distance, speed, clearance, p);
        }

        /// <summary>Driven shots keep the requested pace if they can, and give up speed to clear the net when they cannot.</summary>
        static ShotSolution SolveDriven(Vector3 contact, Vector3 direction, Vector3 spin, float distance, float speed,
            float clearance, in BallPhysicsParams p)
        {
            ShotSolution best = default;
            float bestClearance = float.NegativeInfinity;
            float currentSpeed = speed;

            for (int attempt = 0; attempt < SpeedAttempts && currentSpeed >= speed * MinSpeedFraction; attempt++)
            {
                if (TryFindElevation(contact, direction, spin, distance, currentSpeed, p, out float elevation))
                {
                    Vector3 velocity = LaunchVelocity(direction, currentSpeed, elevation);
                    Fly(contact, velocity, spin, direction, p, out _, out float netClearance);
                    if (netClearance >= clearance)
                        return new ShotSolution { Solved = true, Velocity = velocity, Spin = spin, NetClearance = netClearance };
                    if (netClearance > bestClearance)
                    {
                        bestClearance = netClearance;
                        best = new ShotSolution { Solved = false, Velocity = velocity, Spin = spin, NetClearance = netClearance };
                    }
                }
                currentSpeed *= SpeedBackoff;
            }

            return best.Velocity != Vector3.zero ? best : FallbackShot(direction, speed, spin);
        }

        /// <summary>Arcs (lobs, drop shots) keep a launch angle and solve for speed, steepening until they clear the net.</summary>
        static ShotSolution SolveArc(Vector3 contact, Vector3 direction, Vector3 spin, float distance, float clearance,
            float elevationDegrees, in BallPhysicsParams p)
        {
            ShotSolution best = default;
            for (int attempt = 0; attempt < ElevationAttempts; attempt++)
            {
                float elevation = elevationDegrees + attempt * ElevationBackoffDegrees;
                float low = 2f;
                float high = 80f;
                for (int i = 0; i < BisectionSteps + 4; i++)
                {
                    float mid = (low + high) * 0.5f;
                    Fly(contact, LaunchVelocity(direction, mid, elevation), spin, direction, p, out float range, out _);
                    if (range < distance) low = mid;
                    else high = mid;
                }

                Vector3 velocity = LaunchVelocity(direction, (low + high) * 0.5f, elevation);
                Fly(contact, velocity, spin, direction, p, out _, out float netClearance);
                best = new ShotSolution { Solved = netClearance >= clearance, Velocity = velocity, Spin = spin, NetClearance = netClearance };
                if (best.Solved) return best;
            }
            return best;
        }

        /// <summary>Finds the flattest launch angle that reaches the target at this speed.</summary>
        static bool TryFindElevation(Vector3 contact, Vector3 direction, Vector3 spin, float distance, float speed,
            in BallPhysicsParams p, out float elevation)
        {
            elevation = 0f;
            float previous = MinElevationDegrees;
            Fly(contact, LaunchVelocity(direction, speed, previous), spin, direction, p, out float range, out _);
            if (range >= distance) return false; // Already long at the steepest downward angle.

            for (float current = previous + ScanStepDegrees; current <= MaxElevationDegrees; current += ScanStepDegrees)
            {
                Fly(contact, LaunchVelocity(direction, speed, current), spin, direction, p, out range, out _);
                if (range >= distance)
                {
                    float low = previous;
                    float high = current;
                    for (int i = 0; i < BisectionSteps; i++)
                    {
                        float mid = (low + high) * 0.5f;
                        Fly(contact, LaunchVelocity(direction, speed, mid), spin, direction, p, out range, out _);
                        if (range < distance) low = mid;
                        else high = mid;
                    }
                    elevation = (low + high) * 0.5f;
                    return true;
                }
                previous = current;
            }
            return false;
        }

        /// <summary>Flies the shot to its first landing, ignoring the net but measuring how far above it the ball passes.</summary>
        static void Fly(Vector3 contact, Vector3 velocity, Vector3 spin, Vector3 direction, in BallPhysicsParams p,
            out float range, out float netClearance)
        {
            BallState state = new BallState { Position = contact, Velocity = velocity, Spin = spin };
            netClearance = float.PositiveInfinity;
            int steps = Mathf.CeilToInt(FlightLimit / SolverDt);

            for (int i = 0; i < steps; i++)
            {
                Vector3 before = state.Position;
                BallSimulator.IntegrateFlight(ref state, p, SolverDt);

                if (before.z * state.Position.z < 0f)
                {
                    float t = before.z / (before.z - state.Position.z);
                    float x = Mathf.Lerp(before.x, state.Position.x, t);
                    float y = Mathf.Lerp(before.y, state.Position.y, t);
                    netClearance = y - p.Radius - CourtGeometry.NetHeightAt(x);
                }

                if (state.Position.y <= p.Radius)
                {
                    float t = (before.y - p.Radius) / Mathf.Max(before.y - state.Position.y, 1e-5f);
                    Vector3 landing = Vector3.Lerp(before, state.Position, t);
                    range = Vector3.Dot(landing - contact, direction);
                    return;
                }
            }
            range = float.PositiveInfinity;
        }

        static Vector3 LaunchVelocity(Vector3 direction, float speed, float elevationDegrees)
        {
            float radians = elevationDegrees * Mathf.Deg2Rad;
            return (direction * Mathf.Cos(radians) + Vector3.up * Mathf.Sin(radians)) * speed;
        }

        static ShotSolution FallbackShot(Vector3 direction, float speed, Vector3 spin) =>
            new ShotSolution { Solved = false, Velocity = LaunchVelocity(direction, speed * 0.6f, 12f), Spin = spin };
    }
}
