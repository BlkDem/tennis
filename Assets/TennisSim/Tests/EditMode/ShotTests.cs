using System.Collections.Generic;
using NUnit.Framework;
using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim.Tests
{
    public class ShotTests
    {
        const float Dt = RallySession.TickDt;
        static readonly BallPhysicsParams P = BallPhysicsParams.Default;
        static readonly ShotTuning T = new ShotTuning();

        struct Flight
        {
            public Vector3 Landing;
            public float Apex;
            public float Clearance;
            public float FlightTime;
            public bool HitNet;
        }

        static Flight FlyOut(Vector3 contact, Vector3 velocity, Vector3 spin)
        {
            var state = new BallState { Position = contact, Velocity = velocity, Spin = spin };
            var flight = new Flight { Apex = contact.y, Clearance = float.PositiveInfinity };
            for (int i = 0; i < 1200; i++)
            {
                Vector3 before = state.Position;
                BallContact c = BallSimulator.Step(ref state, P, Dt);
                flight.Apex = Mathf.Max(flight.Apex, state.Position.y);
                if (c.Type == BallContactType.Net) flight.HitNet = true;
                if (before.z * state.Position.z < 0f)
                    flight.Clearance = Mathf.Min(state.Position.y, before.y) - CourtGeometry.NetHeightAt(state.Position.x);
                if (c.Type == BallContactType.Ground)
                {
                    flight.Landing = c.Point;
                    flight.FlightTime = i * Dt;
                    return flight;
                }
            }
            Assert.Fail("The shot never landed.");
            return flight;
        }

        static ShotResult PlanPerfect(ShotButton button, Vector3 contact, bool bounced, Vector2 aim = default, PlayerSlot slot = PlayerSlot.Near)
        {
            var context = new ShotContext
            {
                ContactPoint = contact,
                BallHasBounced = bounced,
                Slot = slot,
                Button = button,
                Side = HandSide.Forehand,
                Contact = new ContactEvaluation { Score = 1f, Quality = ContactQuality.Perfect },
                Aim = aim,
                Preparation = 1f,
            };
            return ShotPlanner.Plan(context, T, P, new System.Random(7));
        }

        static ShotResult PlanCharged(float charge, Vector2? target = null, ShotButton button = ShotButton.Auto)
        {
            var context = new ShotContext
            {
                ContactPoint = new Vector3(0.5f, 1f, -11f),
                BallHasBounced = true,
                Slot = PlayerSlot.Near,
                Button = button,
                Side = HandSide.Forehand,
                Contact = new ContactEvaluation { Score = 0.85f, Quality = ContactQuality.Perfect },
                Target = target,
                Preparation = charge,
            };
            return ShotPlanner.Plan(context, T, P, new System.Random(7));
        }

        [Test]
        public void Charge_HoldingLongerHitsHarder()
        {
            float weak = PlanCharged(0.1f).Velocity.magnitude;
            float half = PlanCharged(0.5f).Velocity.magnitude;
            float full = PlanCharged(1f).Velocity.magnitude;
            TestContext.WriteLine($"km/h: charge 0.1 {weak * 3.6f:F0}, 0.5 {half * 3.6f:F0}, full {full * 3.6f:F0}");
            Assert.That(half, Is.GreaterThan(weak));
            Assert.That(full, Is.GreaterThan(half));
            Assert.That(full / weak, Is.GreaterThan(1.2f), "the difference must be felt, not just measured");
        }

        [Test]
        public void Cursor_TheBallIsAimedWhereTheCursorIs()
        {
            var target = new Vector2(-2.6f, 9.5f);
            ShotResult shot = PlanCharged(1f, target);
            Assert.That(shot.AimedTarget.x, Is.EqualTo(target.x).Within(1e-4f));
            Assert.That(shot.AimedTarget.y, Is.EqualTo(target.y).Within(1e-4f));

            // A drop shot cannot be aimed at the baseline: the cursor is kept to where the shot can land.
            ShotResult drop = PlanCharged(1f, target, ShotButton.Drop);
            Assert.That(drop.AimedTarget.y, Is.EqualTo(T.Profile(ShotKind.Drop).MaxDepth).Within(1e-4f));
        }

        static IEnumerable<ShotKind> DrivenAndArcKinds()
        {
            yield return ShotKind.Topspin;
            yield return ShotKind.Slice;
            yield return ShotKind.Flat;
            yield return ShotKind.Lob;
            yield return ShotKind.Drop;
            yield return ShotKind.Volley;
            yield return ShotKind.Smash;
        }

        [TestCaseSource(nameof(DrivenAndArcKinds))]
        public void Solver_LandsOnTargetAndClearsTheNet(ShotKind kind)
        {
            ShotProfile profile = T.Profile(kind);
            float[] heights = kind == ShotKind.Smash ? new[] { 2.3f, 2.8f } : new[] { 0.6f, 1f, 1.4f };
            float[] contactZ = kind == ShotKind.Volley || kind == ShotKind.Smash ? new[] { -2.5f, -5f } : new[] { -6f, -11f, -13f };

            foreach (float height in heights)
            foreach (float z in contactZ)
            foreach (float targetX in new[] { -3f, 0f, 3f })
            {
                Vector3 contact = new Vector3(-1f, height, z);
                Vector2 target = new Vector2(targetX, profile.DefaultDepth);
                float speed = profile.MaxSpeed;
                ShotSolution s = ShotSolver.Solve(contact, target, speed, profile.Spin, profile.NetClearance, profile.LaunchElevation, P);
                Flight flight = FlyOut(contact, s.Velocity, s.Spin);

                string where = $"{kind} from {contact} to {target}";
                Assert.That(s.Solved, Is.True, where);
                Assert.That(flight.HitNet, Is.False, where);
                Assert.That(Vector2.Distance(new Vector2(flight.Landing.x, flight.Landing.z), target), Is.LessThan(0.25f), where);
            }
        }

        [Test]
        public void Solver_MirrorsForTheFarPlayer()
        {
            ShotResult near = PlanPerfect(ShotButton.Auto, new Vector3(1f, 1f, -11f), true, new Vector2(0.6f, 0f));
            ShotResult far = PlanPerfect(ShotButton.Auto, new Vector3(1f, 1f, 11f), true, new Vector2(0.6f, 0f), PlayerSlot.Far);

            Flight a = FlyOut(new Vector3(1f, 1f, -11f), near.Velocity, near.Spin);
            Flight b = FlyOut(new Vector3(1f, 1f, 11f), far.Velocity, far.Spin);
            Assert.That(a.Landing.z, Is.GreaterThan(5f));
            Assert.That(b.Landing.z, Is.LessThan(-5f));
            Assert.That(a.Landing.x, Is.EqualTo(b.Landing.x).Within(1f));
        }

        [Test]
        public void ShotTypes_HaveDistinctFlights()
        {
            Vector3 contact = new Vector3(0f, 1f, -11.5f);
            Flight Of(ShotButton button)
            {
                ShotResult r = PlanPerfect(button, contact, true);
                return FlyOut(contact, r.Velocity, r.Spin);
            }

            Flight topspin = Of(ShotButton.Auto);
            Flight flat = Of(ShotButton.Flat);
            Flight slice = Of(ShotButton.Slice);
            Flight lob = Of(ShotButton.Lob);
            Flight drop = Of(ShotButton.Drop);

            Assert.That(flat.FlightTime, Is.LessThan(topspin.FlightTime), "flat is the fastest drive");
            Assert.That(topspin.Apex, Is.GreaterThan(flat.Apex), "topspin arcs higher than flat");
            Assert.That(lob.Apex, Is.GreaterThan(6f), "a lob goes high");
            Assert.That(lob.FlightTime, Is.GreaterThan(2f));
            Assert.That(drop.Landing.z, Is.LessThan(5f), "a drop shot lands short");
            Assert.That(slice.FlightTime, Is.GreaterThan(flat.FlightTime));
        }

        [Test]
        public void Spin_MatchesTheShotKind()
        {
            Vector3 contact = new Vector3(0f, 1f, -11.5f);
            Assert.That(PlanPerfect(ShotButton.Auto, contact, true).Spin.x, Is.GreaterThan(100f));
            Assert.That(PlanPerfect(ShotButton.Slice, contact, true).Spin.x, Is.LessThan(-100f));
            Assert.That(Mathf.Abs(PlanPerfect(ShotButton.Flat, contact, true).Spin.x), Is.LessThan(100f));
        }

        [TestCase(ShotButton.Auto, 1.0f, true, ShotKind.Topspin)]
        [TestCase(ShotButton.Forehand, 1.0f, true, ShotKind.Topspin)]
        [TestCase(ShotButton.Backhand, 1.0f, true, ShotKind.Topspin)]
        [TestCase(ShotButton.Slice, 1.0f, true, ShotKind.Slice)]
        [TestCase(ShotButton.Flat, 1.0f, true, ShotKind.Flat)]
        [TestCase(ShotButton.Volley, 1.0f, true, ShotKind.Topspin)]
        [TestCase(ShotButton.Auto, 1.0f, false, ShotKind.Volley)]
        [TestCase(ShotButton.Volley, 1.0f, false, ShotKind.Volley)]
        [TestCase(ShotButton.Auto, 2.5f, false, ShotKind.Smash)]
        [TestCase(ShotButton.Flat, 2.5f, true, ShotKind.Smash)]
        [TestCase(ShotButton.Lob, 2.5f, false, ShotKind.Lob)]
        [TestCase(ShotButton.Drop, 1.0f, false, ShotKind.Drop)]
        public void ResolveKind_FollowsTheBall(ShotButton button, float height, bool bounced, ShotKind expected)
        {
            Assert.That(ShotPlanner.ResolveKind(button, height, bounced, T), Is.EqualTo(expected));
        }

        [TestCase(ShotButton.Auto, 1.0f, true, false, 0.8f, ShotKind.Defensive)]
        [TestCase(ShotButton.Flat, 1.0f, true, false, 0.8f, ShotKind.Defensive)]
        [TestCase(ShotButton.Lob, 1.0f, true, false, 0.8f, ShotKind.Lob)]
        [TestCase(ShotButton.Auto, 0.3f, true, true, 0.2f, ShotKind.HalfVolley)]
        [TestCase(ShotButton.Auto, 0.3f, true, false, 0.2f, ShotKind.Topspin)]
        [TestCase(ShotButton.Slice, 0.8f, true, true, 0.2f, ShotKind.Slice)]
        public void ResolveKind_StretchedAndHalfVolleyShots(ShotButton button, float height, bool bounced, bool rising, float stretch, ShotKind expected)
        {
            Assert.That(ShotPlanner.ResolveKind(button, height, bounced, rising, stretch, T), Is.EqualTo(expected));
        }

        [Test]
        public void Solver_SideSpinCurvesButStillLandsOnTarget()
        {
            Vector3 contact = new Vector3(0.9f, 2.45f, -11.9f);
            Vector2 target = new Vector2(-2.5f, 5.2f);
            ShotProfile slice = T.Profile(ShotKind.ServeSlice);
            ShotSolution s = ShotSolver.Solve(contact, target, slice.MaxSpeed, slice.Spin, slice.NetClearance, 0f, P, slice.SideSpin);
            Flight flight = FlyOut(contact, s.Velocity, s.Spin);

            Assert.That(s.Spin.y, Is.LessThan(-100f), "a slice serve spins about the vertical axis");
            Assert.That(flight.HitNet, Is.False);
            Assert.That(Vector2.Distance(new Vector2(flight.Landing.x, flight.Landing.z), target), Is.LessThan(0.3f));

            // Launched straight at the target, it would curve to the server's left (-X for the near player).
            Vector3 straight = new Vector3(target.x - contact.x, 0f, target.y - contact.z).normalized;
            float launchedAcross = Vector3.Cross(straight, s.Velocity.normalized).y;
            Assert.That(launchedAcross, Is.GreaterThan(0f), "the aim is corrected to the right so the curve brings it back");
        }

        [Test]
        public void Mistiming_PullsTheBallSideways()
        {
            Vector3 contact = new Vector3(0f, 1f, -11.5f);
            float LandingX(float timingError, HandSide side)
            {
                var context = new ShotContext
                {
                    ContactPoint = contact,
                    BallHasBounced = true,
                    Slot = PlayerSlot.Near,
                    Button = ShotButton.Auto,
                    Side = side,
                    Contact = new ContactEvaluation { Score = 0.9f, Quality = ContactQuality.Perfect, TimingError = timingError },
                    Preparation = 1f,
                };
                ShotResult r = ShotPlanner.Plan(context, T, P, new System.Random(3));
                return FlyOut(contact, r.Velocity, r.Spin).Landing.x;
            }

            // The near player's right is +X: an early forehand goes left, a late one right; the backhand is the mirror.
            Assert.That(LandingX(-0.1f, HandSide.Forehand), Is.LessThan(LandingX(0.1f, HandSide.Forehand) - 1f));
            Assert.That(LandingX(-0.1f, HandSide.Backhand), Is.GreaterThan(LandingX(0.1f, HandSide.Backhand) + 1f));
        }

        [Test]
        public void HighBallsAreHitHarderThanLowOnes()
        {
            float Speed(float height) => PlanPerfect(ShotButton.Flat, new Vector3(0f, height, -11.5f), true).Velocity.magnitude;
            Assert.That(Speed(1.8f), Is.GreaterThan(Speed(1.0f)));
            Assert.That(Speed(1.0f), Is.GreaterThan(Speed(0.4f)));
        }

        [TestCase(0f, 0f, 0f, ContactQuality.Perfect)]
        [TestCase(0.02f, 0.1f, 0f, ContactQuality.Perfect)]
        [TestCase(0.06f, 0.05f, 0f, ContactQuality.Good)]
        [TestCase(-0.07f, 0.05f, 0f, ContactQuality.Good)]
        [TestCase(0.02f, 0.1f, 1f, ContactQuality.Good)]
        [TestCase(0.14f, 0.1f, 0f, ContactQuality.Poor)]
        [TestCase(0.05f, 0.9f, 0.5f, ContactQuality.Poor)]
        public void ContactQuality_DependsOnTimingSpacingAndMovement(float timing, float spacing, float move, ContactQuality expected)
        {
            Assert.That(ContactEvaluator.Evaluate(timing, spacing, move, T).Quality, Is.EqualTo(expected));
        }

        [Test]
        public void Quality_DecidesHowOftenAShotStaysIn()
        {
            float InRate(ContactQuality quality, float score)
            {
                var rng = new System.Random(99);
                int inside = 0;
                const int Shots = 300;
                Vector3 contact = new Vector3(0f, 1f, -11.5f);
                for (int i = 0; i < Shots; i++)
                {
                    var context = new ShotContext
                    {
                        ContactPoint = contact,
                        BallHasBounced = true,
                        Slot = PlayerSlot.Near,
                        Button = ShotButton.Auto,
                        Contact = new ContactEvaluation { Score = score, Quality = quality },
                        Aim = new Vector2(0.8f, 0.5f),
                        Preparation = 0.7f,
                    };
                    ShotResult r = ShotPlanner.Plan(context, T, P, rng);
                    Flight f = FlyOut(contact, r.Velocity, r.Spin);
                    if (!f.HitNet && f.Landing.z > 0f && CourtGeometry.IsInsideSingles(f.Landing.x, f.Landing.z)) inside++;
                }
                return inside / (float)Shots;
            }

            float perfect = InRate(ContactQuality.Perfect, 0.95f);
            float good = InRate(ContactQuality.Good, 0.65f);
            float poor = InRate(ContactQuality.Poor, 0.3f);
            TestContext.WriteLine($"in-court rate: perfect {perfect:P0}, good {good:P0}, poor {poor:P0}");

            Assert.That(perfect, Is.GreaterThan(0.93f));
            Assert.That(good, Is.GreaterThan(0.72f).And.LessThan(perfect + 0.001f));
            Assert.That(poor, Is.GreaterThan(0.15f).And.LessThan(good));
        }
    }
}
