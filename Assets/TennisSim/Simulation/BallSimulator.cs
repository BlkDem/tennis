using System;
using UnityEngine;

namespace TennisSim.Simulation
{
    [Serializable]
    public struct BallPhysicsParams
    {
        [Header("Ball")]
        public float Mass;
        public float Radius;

        [Header("Air")]
        public float Gravity;
        public float AirDensity;
        public float DragCoefficient;
        [Tooltip("Scales the lift produced by spin. 1 = measured tennis ball data.")]
        public float MagnusScale;
        [Tooltip("Fraction of spin lost per second of flight.")]
        public float SpinDecayPerSecond;

        [Header("Court bounce")]
        [Tooltip("Vertical coefficient of restitution for a slow impact.")]
        public float Restitution;
        [Tooltip("Restitution lost per m/s of impact speed: hard impacts are less elastic.")]
        public float RestitutionSpeedLoss;
        public float MinRestitution;
        [Tooltip("Coulomb friction between ball and surface. Hard court is around 0.6.")]
        public float Friction;
        [Tooltip("Below this vertical speed the ball stops bouncing and rolls.")]
        public float RollSpeedThreshold;
        public float RollingDeceleration;

        [Header("Net")]
        public float NetRestitution;
        public float NetTangentialDamping;
        public float NetCordForwardDamping;
        public float NetCordRestitution;

        public static BallPhysicsParams Default => new BallPhysicsParams
        {
            Mass = 0.057f,
            Radius = 0.0335f,
            Gravity = 9.81f,
            AirDensity = 1.21f,
            DragCoefficient = 0.55f,
            MagnusScale = 1f,
            SpinDecayPerSecond = 0.04f,
            Restitution = 0.80f,
            RestitutionSpeedLoss = 0.006f,
            MinRestitution = 0.55f,
            Friction = 0.62f,
            RollSpeedThreshold = 0.35f,
            RollingDeceleration = 1.6f,
            NetRestitution = 0.12f,
            NetTangentialDamping = 0.35f,
            NetCordForwardDamping = 0.55f,
            NetCordRestitution = 0.35f,
        };
    }

    public struct BallState
    {
        public Vector3 Position;
        public Vector3 Velocity;
        /// <summary>Angular velocity in rad/s. Surface velocity is Cross(Spin, r).</summary>
        public Vector3 Spin;
    }

    public enum BallContactType { None, Ground, Net, NetCord }

    public struct BallContact
    {
        public BallContactType Type;
        public Vector3 Point;
        public float ImpactSpeed;
    }

    /// <summary>
    /// Fixed-step tennis ball physics: drag, Magnus lift, spin-aware bounce and a swept net test.
    /// Pure and deterministic, so prediction, the shot solver and the live ball share one code path.
    /// </summary>
    public static class BallSimulator
    {
        /// <summary>Thin-shell inertia of a tennis ball: I = 2/3 m r².</summary>
        const float InertiaFactor = 2f / 3f;
        /// <summary>Fraction of the slip speed removed by an impact that ends in rolling (2/5 for a thin shell).</summary>
        const float GripFactor = InertiaFactor / (1f + InertiaFactor);
        const float ZeroCrossingNudge = 0.001f;

        public static Vector3 Acceleration(in Vector3 velocity, in Vector3 spin, in BallPhysicsParams p)
        {
            Vector3 acceleration = new Vector3(0f, -p.Gravity, 0f);
            float speed = velocity.magnitude;
            if (speed < 1e-4f) return acceleration;

            float area = Mathf.PI * p.Radius * p.Radius;
            float dynamicPressureOverMass = 0.5f * p.AirDensity * area * speed / p.Mass;
            acceleration -= velocity * (p.DragCoefficient * dynamicPressureOverMass);

            float spinRate = spin.magnitude;
            if (spinRate > 1e-3f)
            {
                // Lift coefficient fit from tennis ball wind tunnel data: Cl = 1 / (2 + v / (r * w)).
                float lift = p.MagnusScale / (2f + speed / (p.Radius * spinRate));
                acceleration += Vector3.Cross(spin / spinRate, velocity) * (lift * dynamicPressureOverMass);
            }
            return acceleration;
        }

        /// <summary>Advances the ball through the air without any collision (midpoint integration).</summary>
        public static void IntegrateFlight(ref BallState s, in BallPhysicsParams p, float dt)
        {
            Vector3 a1 = Acceleration(s.Velocity, s.Spin, p);
            Vector3 midVelocity = s.Velocity + a1 * (dt * 0.5f);
            Vector3 a2 = Acceleration(midVelocity, s.Spin, p);
            s.Position += midVelocity * dt;
            s.Velocity += a2 * dt;
            s.Spin *= Mathf.Max(0f, 1f - p.SpinDecayPerSecond * dt);
        }

        /// <summary>
        /// Advances the ball by one step and resolves at most one contact.
        /// Tests are swept along the step, so the ball cannot tunnel through the court or the net.
        /// </summary>
        public static BallContact Step(ref BallState s, in BallPhysicsParams p, float dt)
        {
            BallState start = s;
            bool rolling = start.Position.y <= p.Radius + 1e-3f && Mathf.Abs(start.Velocity.y) < p.RollSpeedThreshold;

            if (rolling) IntegrateRolling(ref s, p, dt);
            else IntegrateFlight(ref s, p, dt);

            float groundFraction = !rolling && s.Position.y < p.Radius
                ? Mathf.Clamp01((start.Position.y - p.Radius) / (start.Position.y - s.Position.y))
                : float.PositiveInfinity;

            if (start.Position.z * s.Position.z < 0f)
            {
                float netFraction = start.Position.z / (start.Position.z - s.Position.z);
                if (netFraction < groundFraction && TryResolveNet(start, ref s, p, dt, netFraction, rolling, out BallContact netContact))
                    return netContact;
            }

            if (float.IsPositiveInfinity(groundFraction)) return default;

            s = start;
            IntegrateFlight(ref s, p, dt * groundFraction);
            s.Position.y = p.Radius;
            float impactSpeed = -s.Velocity.y;
            Vector3 point = new Vector3(s.Position.x, 0f, s.Position.z);
            ResolveBounce(ref s, p, impactSpeed);

            // The rest of the step can still carry the ball into the net.
            BallState afterBounce = s;
            float remaining = dt * (1f - groundFraction);
            IntegrateFlight(ref s, p, remaining);
            if (afterBounce.Position.z * s.Position.z < 0f)
            {
                float fraction = afterBounce.Position.z / (afterBounce.Position.z - s.Position.z);
                TryResolveNet(afterBounce, ref s, p, remaining, fraction, false, out _);
            }
            if (s.Position.y < p.Radius) s.Position.y = p.Radius;

            // Tiny hops at the end of a bounce series are rolling, not bounces worth reporting.
            return impactSpeed < p.RollSpeedThreshold
                ? default
                : new BallContact { Type = BallContactType.Ground, Point = point, ImpactSpeed = impactSpeed };
        }

        static void IntegrateRolling(ref BallState s, in BallPhysicsParams p, float dt)
        {
            Vector3 horizontal = new Vector3(s.Velocity.x, 0f, s.Velocity.z);
            float speed = horizontal.magnitude;
            float newSpeed = Mathf.Max(0f, speed - p.RollingDeceleration * dt);
            horizontal = speed > 1e-4f ? horizontal * (newSpeed / speed) : Vector3.zero;
            s.Velocity = horizontal;
            s.Position += horizontal * dt;
            s.Position.y = p.Radius;
            // Rolling without slipping: the contact point is at rest.
            s.Spin = Vector3.Cross(Vector3.up, horizontal) / p.Radius;
        }

        /// <summary>Impulse-based impact with Coulomb friction: spin changes both the kick and the skid.</summary>
        static void ResolveBounce(ref BallState s, in BallPhysicsParams p, float impactSpeed)
        {
            float restitution = Mathf.Max(p.MinRestitution, p.Restitution - p.RestitutionSpeedLoss * impactSpeed);
            float normalImpulsePerMass = (1f + restitution) * impactSpeed;

            Vector3 lever = new Vector3(0f, -p.Radius, 0f);
            Vector3 tangential = new Vector3(s.Velocity.x, 0f, s.Velocity.z);
            Vector3 slip = tangential + Vector3.Cross(s.Spin, lever);
            float slipSpeed = slip.magnitude;

            if (slipSpeed > 1e-4f)
            {
                float frictionPerMass = Mathf.Min(p.Friction * normalImpulsePerMass, GripFactor * slipSpeed);
                Vector3 impulsePerMass = slip * (-frictionPerMass / slipSpeed);
                tangential += impulsePerMass;
                s.Spin += Vector3.Cross(lever, impulsePerMass) / (InertiaFactor * p.Radius * p.Radius);
            }

            s.Velocity = tangential + Vector3.up * (restitution * impactSpeed);
        }

        static bool TryResolveNet(in BallState start, ref BallState s, in BallPhysicsParams p, float dt, float fraction, bool rolling, out BallContact contact)
        {
            contact = default;
            float x = Mathf.Lerp(start.Position.x, s.Position.x, fraction);
            float y = Mathf.Lerp(start.Position.y, s.Position.y, fraction);
            if (Mathf.Abs(x) > CourtGeometry.NetHalfWidth) return false;

            float netHeight = CourtGeometry.NetHeightAt(x);
            if (y - p.Radius >= netHeight) return false;

            float direction = Mathf.Sign(s.Position.z - start.Position.z);
            BallState hit = start;
            if (rolling) IntegrateRolling(ref hit, p, dt * fraction);
            else IntegrateFlight(ref hit, p, dt * fraction);
            float impactSpeed = Mathf.Abs(hit.Velocity.z);

            if (y >= netHeight)
            {
                // Clips the tape: the ball pops up and carries over, slowed.
                hit.Velocity.y = Mathf.Abs(hit.Velocity.y) * p.NetCordRestitution + impactSpeed * 0.08f;
                hit.Velocity.z *= p.NetCordForwardDamping;
                hit.Velocity.x *= 0.8f;
                hit.Spin *= 0.5f;
                hit.Position.y = netHeight + p.Radius;
                hit.Position.z = direction * ZeroCrossingNudge;
                contact.Type = BallContactType.NetCord;
            }
            else
            {
                // Into the fabric: the net swallows the energy and the ball drops on the hitter's side.
                hit.Velocity.z = -hit.Velocity.z * p.NetRestitution;
                hit.Velocity.x *= p.NetTangentialDamping;
                hit.Velocity.y *= p.NetTangentialDamping;
                hit.Spin *= 0.2f;
                hit.Position.y = Mathf.Max(y, p.Radius);
                hit.Position.z = -direction * (p.Radius + ZeroCrossingNudge);
                contact.Type = BallContactType.Net;
            }

            contact.Point = new Vector3(x, y, 0f);
            contact.ImpactSpeed = impactSpeed;
            s = hit;
            return true;
        }
    }
}
