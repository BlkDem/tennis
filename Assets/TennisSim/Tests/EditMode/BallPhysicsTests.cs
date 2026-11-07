using NUnit.Framework;
using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim.Tests
{
    public class BallPhysicsTests
    {
        const float Dt = RallySession.TickDt;
        static readonly BallPhysicsParams P = BallPhysicsParams.Default;

        static BallState Launch(float speed, float elevationDegrees, float spinRate, float startZ = -11f, float startY = 1f)
        {
            float radians = elevationDegrees * Mathf.Deg2Rad;
            Vector3 direction = new Vector3(0f, Mathf.Sin(radians), Mathf.Cos(radians));
            return new BallState
            {
                Position = new Vector3(0f, startY, startZ),
                Velocity = direction * speed,
                Spin = Vector3.right * spinRate,
            };
        }

        /// <summary>Flies until the first contact of the wanted type or a time limit, returning the state just after it.</summary>
        static BallState FlyUntil(BallState state, BallContactType wanted, out BallContact contact, float limit = 8f)
        {
            contact = default;
            int steps = Mathf.CeilToInt(limit / Dt);
            for (int i = 0; i < steps; i++)
            {
                contact = BallSimulator.Step(ref state, P, Dt);
                if (contact.Type == wanted) return state;
            }
            Assert.Fail($"No {wanted} contact within {limit} s.");
            return state;
        }

        [Test]
        public void DroppedBall_FallsAtGravityAndReboundsLower()
        {
            var state = new BallState { Position = new Vector3(0f, 2f, -5f) };
            BallState after = FlyUntil(state, BallContactType.Ground, out BallContact contact);

            Assert.That(contact.ImpactSpeed, Is.EqualTo(Mathf.Sqrt(2f * P.Gravity * 2f)).Within(0.2f));
            Assert.That(after.Velocity.y, Is.GreaterThan(0f));
            Assert.That(after.Velocity.y, Is.LessThan(contact.ImpactSpeed));
        }

        [Test]
        public void Drag_SlowsTheBallInFlight()
        {
            BallState state = Launch(40f, 5f, 0f);
            for (int i = 0; i < 60; i++) BallSimulator.IntegrateFlight(ref state, P, Dt);
            Assert.That(state.Velocity.z, Is.LessThan(39f));
        }

        [Test]
        public void Magnus_TopspinDipsAndBackspinCarries()
        {
            float Range(float spin)
            {
                BallState after = FlyUntil(Launch(25f, 8f, spin), BallContactType.Ground, out BallContact contact);
                return contact.Point.z;
            }

            float flat = Range(0f);
            Assert.That(Range(300f), Is.LessThan(flat - 1f), "topspin should land shorter");
            Assert.That(Range(-200f), Is.GreaterThan(flat + 1f), "backspin should carry further");
        }

        [Test]
        public void Bounce_TopspinKicksForwardAndSliceSkidsSlower()
        {
            float SpeedAfterBounce(float spin)
            {
                var state = new BallState
                {
                    Position = new Vector3(0f, 1f, 2f),
                    Velocity = new Vector3(0f, -8f, 22f),
                    Spin = Vector3.right * spin,
                };
                return FlyUntil(state, BallContactType.Ground, out _).Velocity.z;
            }

            Assert.That(SpeedAfterBounce(350f), Is.GreaterThan(SpeedAfterBounce(0f)));
            Assert.That(SpeedAfterBounce(0f), Is.GreaterThan(SpeedAfterBounce(-250f)));
        }

        [Test]
        public void Bounce_FrictionTurnsSlipIntoRolling()
        {
            var state = new BallState { Position = new Vector3(0f, 0.5f, 2f), Velocity = new Vector3(0f, -5f, 20f) };
            BallState after = FlyUntil(state, BallContactType.Ground, out _);
            Assert.That(after.Spin.x, Is.GreaterThan(0f), "a sliding ball picks up forward roll");
        }

        [Test]
        public void Net_BallIntoFabricStaysOnHittersSideAndDropsOut()
        {
            BallState state = Launch(30f, 0f, 0f, -5f, 0.6f);
            BallState after = FlyUntil(state, BallContactType.Net, out _);
            Assert.That(after.Position.z, Is.LessThan(0f));

            // The ball must fall to the ground on the hitter's side and never get stuck or pass through.
            for (int i = 0; i < 1200; i++)
            {
                BallSimulator.Step(ref after, P, Dt);
                Assert.That(after.Position.z, Is.LessThan(0f));
            }
            Assert.That(after.Position.y, Is.EqualTo(P.Radius).Within(0.01f));
            Assert.That(after.Velocity.magnitude, Is.LessThan(0.5f));
        }

        [Test]
        public void Net_HighBallPassesCleanly()
        {
            BallState state = Launch(25f, 12f, 0f);
            for (int i = 0; i < 90; i++)
            {
                BallContact contact = BallSimulator.Step(ref state, P, Dt);
                Assert.That(contact.Type, Is.Not.EqualTo(BallContactType.Net));
                Assert.That(contact.Type, Is.Not.EqualTo(BallContactType.NetCord));
            }
            Assert.That(state.Position.z, Is.GreaterThan(0f));
        }

        [Test]
        public void Net_TapeContactCarriesTheBallOverSlowly()
        {
            var state = new BallState
            {
                // Starts high enough that gravity brings it down onto the tape as it reaches the net.
                Position = new Vector3(0f, CourtGeometry.NetCenterHeight + 0.03f, -1f),
                Velocity = new Vector3(0f, 0f, 20f),
            };
            BallState after = FlyUntil(state, BallContactType.NetCord, out _);
            Assert.That(after.Position.z, Is.GreaterThan(0f));
            Assert.That(after.Velocity.z, Is.LessThan(12f));
        }

        [Test]
        public void Stability_ThousandsOfRandomShotsNeverTunnelOrExplode()
        {
            var rng = new System.Random(1234);
            float R(double min, double max) => (float)(min + rng.NextDouble() * (max - min));

            for (int shot = 0; shot < 2000; shot++)
            {
                var state = new BallState
                {
                    Position = new Vector3(R(-5, 5), R(0.1, 3), R(-14, -0.5)),
                    Velocity = new Vector3(R(-8, 8), R(-15, 25), R(5, 70)),
                    Spin = new Vector3(R(-500, 500), R(-100, 100), R(-100, 100)),
                };

                float lastSpeed = float.PositiveInfinity;
                for (int i = 0; i < 1200; i++)
                {
                    float zBefore = state.Position.z;
                    float yBefore = state.Position.y;
                    BallContact contact = BallSimulator.Step(ref state, P, Dt);

                    Assert.That(state.Position.y, Is.GreaterThanOrEqualTo(P.Radius - 1e-3f), $"shot {shot} sank through the court");
                    Assert.That(float.IsNaN(state.Position.x + state.Position.y + state.Position.z), Is.False);
                    Assert.That(state.Velocity.magnitude, Is.LessThan(90f), $"shot {shot} gained energy");

                    bool crossed = zBefore < 0f && state.Position.z >= 0f;
                    if (crossed && Mathf.Abs(state.Position.x) < CourtGeometry.NetHalfWidth - 0.5f)
                    {
                        // Interpolated crossing height; the net cord pushes the ball to the tape, so allow its radius.
                        float y = Mathf.Min(yBefore, state.Position.y);
                        bool cleared = contact.Type == BallContactType.NetCord
                            || Mathf.Max(yBefore, state.Position.y) + P.Radius >= CourtGeometry.NetHeightAt(state.Position.x);
                        Assert.That(cleared, Is.True, $"shot {shot} passed through the net at y={y}");
                    }
                    lastSpeed = state.Velocity.magnitude;
                }
                Assert.That(lastSpeed, Is.LessThan(40f));
            }
        }

        [Test]
        public void Prediction_MatchesTheLiveBallExactly()
        {
            BallState state = Launch(30f, 7f, 280f);
            var trajectory = new BallTrajectory();
            trajectory.Compute(state, 0f, P, Dt, 3f);

            foreach (TrajectorySample sample in trajectory.Samples)
            {
                BallSimulator.Step(ref state, P, Dt);
                Assert.That(Vector3.Distance(sample.State.Position, state.Position), Is.LessThan(1e-4f));
            }
            Assert.That(trajectory.HasFirstBounce, Is.True);
        }
    }
}
