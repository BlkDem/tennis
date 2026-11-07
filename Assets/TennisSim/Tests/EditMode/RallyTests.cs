using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim.Tests
{
    public class RallyTests
    {
        const float Dt = RallySession.TickDt;

        static RallySession NewSession(int seed) =>
            new RallySession(BallPhysicsParams.Default, new ShotTuning(), new PlayerMovementTuning(), seed);

        [Test]
        public void Referee_BallOutGivesThePointToTheOpponent()
        {
            var referee = new RallyReferee();
            referee.BeginFeed(PlayerSlot.Near);
            referee.OnGround(new Vector3(0f, 0f, -11f));
            referee.OnHit(PlayerSlot.Near);
            referee.OnGround(new Vector3(0f, 0f, 13f));

            Assert.That(referee.IsFinished);
            Assert.That(referee.Winner, Is.EqualTo(PlayerSlot.Far));
            Assert.That(referee.Reason, Is.EqualTo(PointEndReason.Out));
        }

        [Test]
        public void Referee_LineIsIn()
        {
            var referee = new RallyReferee();
            referee.BeginFeed(PlayerSlot.Near);
            referee.OnHit(PlayerSlot.Near);
            referee.OnGround(new Vector3(CourtGeometry.HalfSinglesWidth, 0f, CourtGeometry.HalfLength));
            Assert.That(referee.IsFinished, Is.False);
        }

        [Test]
        public void Referee_DoublesAlleyIsOutInSingles()
        {
            var referee = new RallyReferee();
            referee.BeginFeed(PlayerSlot.Near);
            referee.OnHit(PlayerSlot.Near);
            referee.OnGround(new Vector3(CourtGeometry.HalfSinglesWidth + 0.5f, 0f, 8f));
            Assert.That(referee.Reason, Is.EqualTo(PointEndReason.Out));
        }

        [Test]
        public void Referee_DoubleBounceGivesThePointToTheHitter()
        {
            var referee = new RallyReferee();
            referee.BeginFeed(PlayerSlot.Near);
            referee.OnHit(PlayerSlot.Near);
            referee.OnGround(new Vector3(0f, 0f, 8f));
            Assert.That(referee.CanHit(PlayerSlot.Far), Is.True);
            Assert.That(referee.CanHit(PlayerSlot.Near), Is.False);
            referee.OnGround(new Vector3(0f, 0f, 11f));

            Assert.That(referee.Winner, Is.EqualTo(PlayerSlot.Near));
            Assert.That(referee.Reason, Is.EqualTo(PointEndReason.DoubleBounce));
        }

        [Test]
        public void Referee_NetFaultLosesThePoint()
        {
            var referee = new RallyReferee();
            referee.BeginFeed(PlayerSlot.Far);
            referee.OnHit(PlayerSlot.Far);
            referee.OnNetFabric();
            referee.OnGround(new Vector3(0f, 0f, 0.3f));

            Assert.That(referee.Winner, Is.EqualTo(PlayerSlot.Near));
            Assert.That(referee.Reason, Is.EqualTo(PointEndReason.Net));
        }

        [Test]
        public void Referee_ADroppedServeNeverEndsThePoint()
        {
            var referee = new RallyReferee();
            referee.BeginFeed(PlayerSlot.Near);
            Assert.That(referee.CanHit(PlayerSlot.Near), Is.True);
            Assert.That(referee.CanHit(PlayerSlot.Far), Is.False);
            referee.OnGround(new Vector3(0f, 0f, -12f));
            referee.OnGround(new Vector3(0f, 0f, -12f));
            Assert.That(referee.IsFinished, Is.False);
            Assert.That(referee.FeedExpired, Is.True);
        }

        [Test]
        public void Movement_ReachesTopSpeedQuicklyAndReversesFast()
        {
            var tuning = new PlayerMovementTuning();
            var movement = new PlayerMovement(PlayerSlot.Near, tuning);
            movement.Teleport(new Vector3(0f, 0f, -11f));

            float t = 0f;
            while (movement.Velocity.magnitude < tuning.RunSpeed - 0.01f)
            {
                movement.Tick(Vector3.right * tuning.RunSpeed, Dt);
                t += Dt;
                Assert.That(t, Is.LessThan(0.4f), "too heavy to accelerate");
            }

            t = 0f;
            while (movement.Velocity.x > 0f)
            {
                movement.Tick(Vector3.left * tuning.RunSpeed, Dt);
                t += Dt;
                Assert.That(t, Is.LessThan(0.2f), "too heavy to change direction");
            }
        }

        [Test]
        public void Movement_CannotCrossTheNet()
        {
            var movement = new PlayerMovement(PlayerSlot.Near, new PlayerMovementTuning());
            movement.Teleport(new Vector3(0f, 0f, -2f));
            for (int i = 0; i < 600; i++) movement.Tick(Vector3.forward * 8f, Dt);
            Assert.That(movement.Position.z, Is.LessThan(-0.5f));
        }

        [Test]
        public void Session_TwoBotsPlayCompleteRallies()
        {
            RallySession session = NewSession(42);
            session.Agent(PlayerSlot.Near).IntentSource = new PartnerBot(new System.Random(1));
            session.Agent(PlayerSlot.Far).IntentSource = new PartnerBot(new System.Random(2));

            var rallyLengths = new List<int>();
            var kinds = new Dictionary<ShotKind, int>();
            var qualities = new Dictionary<ContactQuality, int>();
            var reasons = new Dictionary<PointEndReason, int>();
            int hitsThisRally = 0;

            int ticks = (int)(3000f / Dt);
            for (int i = 0; i < ticks && rallyLengths.Count < 150; i++)
            {
                session.Tick(Dt);

                BallState ball = session.Ball;
                Assert.That(float.IsNaN(ball.Position.magnitude), Is.False);
                Assert.That(ball.Position.y, Is.GreaterThanOrEqualTo(BallPhysicsParams.Default.Radius - 1e-3f));

                foreach (RallyEvent e in session.Events)
                {
                    if (e.Type == RallyEventType.Hit)
                    {
                        hitsThisRally++;
                        kinds[e.Shot.Kind] = kinds.TryGetValue(e.Shot.Kind, out int k) ? k + 1 : 1;
                        qualities[e.Shot.Quality] = qualities.TryGetValue(e.Shot.Quality, out int q) ? q + 1 : 1;
                    }
                    else if (e.Type == RallyEventType.PointEnded)
                    {
                        rallyLengths.Add(hitsThisRally);
                        hitsThisRally = 0;
                        reasons[e.Reason] = reasons.TryGetValue(e.Reason, out int r) ? r + 1 : 1;
                    }
                }
            }

            string Show<T>(Dictionary<T, int> d) => string.Join(", ", d.Select(p => $"{p.Key}={p.Value}"));
            TestContext.WriteLine($"points {rallyLengths.Count}, mean hits {rallyLengths.Average():F1}, max {rallyLengths.Max()}");
            TestContext.WriteLine($"kinds: {Show(kinds)}");
            TestContext.WriteLine($"quality: {Show(qualities)}");
            TestContext.WriteLine($"ends: {Show(reasons)}");
            TestContext.WriteLine($"score {session.Points(PlayerSlot.Near)}:{session.Points(PlayerSlot.Far)}");

            Assert.That(rallyLengths.Count, Is.GreaterThanOrEqualTo(100), "points must keep finishing");
            Assert.That(rallyLengths.Average(), Is.InRange(5f, 25f), "rallies should be sustained, but must end");
            Assert.That(rallyLengths.Max(), Is.GreaterThanOrEqualTo(12));
            Assert.That(session.Points(PlayerSlot.Near) + session.Points(PlayerSlot.Far), Is.EqualTo(rallyLengths.Count));
            foreach (ShotKind kind in new[] { ShotKind.Topspin, ShotKind.Slice, ShotKind.Flat, ShotKind.Lob, ShotKind.Drop, ShotKind.Smash })
                Assert.That(kinds.ContainsKey(kind), Is.True, $"{kind} never happened");
            Assert.That(reasons.ContainsKey(PointEndReason.Out) && reasons.ContainsKey(PointEndReason.Net), Is.True, "points should end in different ways");
        }

        /// <summary>A human stand-in: presses when the ball is hit and releases at a chosen offset from the ideal moment.</summary>
        sealed class TimedSwinger : IPlayerIntentSource
        {
            public float ReleaseOffset;
            public bool HoldThrough;
            public PlayerAgent Agent;
            public Vector2 Move;
            bool held;
            bool released;
            int revision = -1;

            public PlayerIntent ReadIntent(in PlayerPerception p)
            {
                var intent = new PlayerIntent { Move = Move, Sprint = true };
                if (p.AwaitingServe)
                {
                    if (p.IsServer) intent.Pressed = ShotButton.Auto;
                    held = p.IsServer;
                    return intent;
                }

                if (p.BallRevision != revision)
                {
                    revision = p.BallRevision;
                    released = false;
                }

                if (p.CanHit && !held && !released)
                {
                    intent.Pressed = ShotButton.Auto;
                    held = true;
                    return intent;
                }

                ContactPlan plan = Agent.Shot.Plan;
                if (held && !HoldThrough && plan.Valid && p.Now >= plan.Time - new ShotTuning().SwingLatency + ReleaseOffset - 0.0001f)
                {
                    intent.Released = ShotButton.Auto;
                    held = false;
                    released = true;
                }
                return intent;
            }
        }

        /// <summary>Plays the serve, then reports the quality of the near player's first return of a real incoming shot.</summary>
        static RallyEvent? FirstReturn(TimedSwinger swinger, int seed)
        {
            RallySession session = NewSession(seed);
            PlayerAgent near = session.Agent(PlayerSlot.Near);
            swinger.Agent = near;
            near.IntentSource = swinger;
            session.Agent(PlayerSlot.Far).IntentSource = new PartnerBot(new System.Random(seed));

            // The far bot serves on the second point, so the first ball the near player meets is a true incoming shot.
            int nearHits = 0;
            for (int i = 0; i < (int)(40f / Dt); i++)
            {
                session.Tick(Dt);
                foreach (RallyEvent e in session.Events)
                {
                    if (e.Type == RallyEventType.Hit && e.Slot == PlayerSlot.Near && session.RallyHits >= 2)
                    {
                        nearHits++;
                        return e;
                    }
                }
            }
            return null;
        }

        static float MeanScore(float offset, bool hold)
        {
            float total = 0f;
            int count = 0;
            for (int seed = 1; seed <= 12; seed++)
            {
                RallyEvent? e = FirstReturn(new TimedSwinger { ReleaseOffset = offset, HoldThrough = hold }, seed);
                if (e.HasValue)
                {
                    total += e.Value.Shot.Score;
                    count++;
                }
            }
            Assert.That(count, Is.GreaterThan(0), $"no contact with offset {offset}");
            return total / count;
        }

        [Test]
        public void Timing_PreciseReleaseBeatsHoldingAndBadTimingIsPunished()
        {
            float precise = MeanScore(0f, false);
            float held = MeanScore(0f, true);
            float early = MeanScore(-0.1f, false);
            TestContext.WriteLine($"score: precise {precise:F2}, held {held:F2}, early {early:F2}");

            Assert.That(precise, Is.GreaterThanOrEqualTo(new ShotTuning().PerfectScore));
            Assert.That(held, Is.LessThan(precise));
            Assert.That(early, Is.LessThan(held));
        }

        /// <summary>A ball flying straight at a motionless player, who taps the button at an offset from the ideal moment.</summary>
        static ShotResult? TapAt(float offset)
        {
            var tuning = new ShotTuning();
            var movement = new PlayerMovementTuning();
            var physics = BallPhysicsParams.Default;
            var controller = new PlayerShotController(PlayerSlot.Near, tuning, movement, physics, new System.Random(1));

            Vector3 feet = new Vector3(0f, 0f, -11f);
            var ball = new BallState { Position = new Vector3(0.85f, 2.2f, 0f), Velocity = new Vector3(0f, 0.5f, -24f) };
            var trajectory = new BallTrajectory();
            trajectory.Compute(ball, 0f, physics, Dt, 3f);
            float strikeZ = feet.z + tuning.StrikeForward;
            float passTime = trajectory.Samples.First(s => s.State.Position.z <= strikeZ).Time;
            float tapTime = passTime - tuning.SwingLatency + offset;

            ShotResult? result = null;
            bool pressed = false;
            bool released = false;
            int bounces = 0;
            for (int i = 0; i < 300 && result == null; i++)
            {
                float now = i * Dt;
                var intent = new PlayerIntent();
                if (!pressed && now >= tapTime)
                {
                    intent.Pressed = ShotButton.Forehand;
                    pressed = true;
                }
                else if (pressed && !released)
                {
                    intent.Released = ShotButton.Forehand;
                    released = true;
                }

                var perception = new PlayerPerception
                {
                    Now = now, Slot = PlayerSlot.Near, Position = feet, Ball = ball, Trajectory = trajectory,
                    BouncesSinceHit = bounces, BallRevision = 1, CanHit = true,
                };
                result = controller.Tick(perception, intent, Vector3.zero, 7f, Vector2.zero);
                if (BallSimulator.Step(ref ball, physics, Dt).Type == BallContactType.Ground) bounces++;
            }
            return result;
        }

        [Test]
        public void Timing_TappedSwingIsGradedByItsMoment()
        {
            Assert.That(TapAt(0f)?.Quality, Is.EqualTo(ContactQuality.Perfect));
            Assert.That(TapAt(0.05f)?.Quality, Is.EqualTo(ContactQuality.Good));
            Assert.That(TapAt(-0.1f)?.Quality, Is.EqualTo(ContactQuality.Good));
            Assert.That(TapAt(0.12f)?.Quality, Is.EqualTo(ContactQuality.Poor));
            Assert.That(TapAt(-0.16f)?.Quality, Is.EqualTo(ContactQuality.Poor));
            Assert.That(TapAt(0.3f).HasValue, Is.False, "swinging after the ball has passed misses it");
            Assert.That(TapAt(-0.5f).HasValue, Is.False, "swinging long before the ball arrives misses it");
        }

        [Test]
        public void ManualMode_PlayerWithoutAutoPositioningStaysWhereTheStickSays()
        {
            RallySession session = NewSession(3);
            PlayerAgent near = session.Agent(PlayerSlot.Near);
            near.AutoPositioning = false;
            var swinger = new TimedSwinger { HoldThrough = true, Agent = near };
            near.IntentSource = swinger;
            session.Agent(PlayerSlot.Far).IntentSource = new PartnerBot(new System.Random(5));

            // Serve ball needs no running; afterwards a motionless stick must mean a motionless player.
            for (int i = 0; i < (int)(1f / Dt); i++) session.Tick(Dt);
            Vector3 before = near.Position;
            near.Movement.Teleport(before + Vector3.right * 2f);
            Vector3 anchor = near.Position;
            for (int i = 0; i < (int)(0.4f / Dt); i++) session.Tick(Dt);
            Assert.That(Vector3.Distance(anchor, near.Position), Is.LessThan(2.1f));
        }

        [Test]
        public void Volley_ButtonTakesTheBallInTheAir()
        {
            var tuning = new ShotTuning();
            var movement = new PlayerMovementTuning();
            var state = new BallState { Position = new Vector3(0f, 1.4f, 9f), Velocity = new Vector3(0f, 2f, -24f), Spin = Vector3.zero };
            var trajectory = new BallTrajectory();
            trajectory.Compute(state, 0f, BallPhysicsParams.Default, Dt, 3f);

            Vector3 nearNet = new Vector3(0f, 0f, -3f);
            Vector3 deep = new Vector3(0f, 0f, -12f);
            ContactPlan volley = ContactPlanner.Find(trajectory, nearNet, PlayerSlot.Near, null, ShotButton.Volley, 0f, 7f, movement, tuning);
            ContactPlan fromNet = ContactPlanner.Find(trajectory, nearNet, PlayerSlot.Near, null, ShotButton.Auto, 0f, 7f, movement, tuning);
            ContactPlan fromBaseline = ContactPlanner.Find(trajectory, deep, PlayerSlot.Near, null, ShotButton.Auto, 0f, 7f, movement, tuning);

            Assert.That(volley.Valid && volley.Reachable, Is.True);
            Assert.That(volley.BallHasBounced, Is.False);
            Assert.That(fromNet.BallHasBounced, Is.False, "a player who came in does not run back to let the ball bounce");
            Assert.That(fromBaseline.BallHasBounced, Is.True, "from the baseline the player lets the ball bounce");
        }

        /// <summary>A player holding a shot button and pushing the stick, like a human running in for the ball.</summary>
        sealed class StickWhilePreparing : IPlayerIntentSource
        {
            public Vector2 Move;
            bool held;

            public PlayerIntent ReadIntent(in PlayerPerception p)
            {
                var intent = new PlayerIntent { Move = Move };
                if (p.AwaitingServe) return intent;
                if (p.CanHit && !held)
                {
                    intent.Pressed = ShotButton.Auto;
                    held = true;
                }
                return intent;
            }
        }

        /// <summary>The near player waits at the baseline for a deep ball from the far side, with no serve in the way.</summary>
        static (RallySession session, PlayerAgent near) IncomingDeepBall(IPlayerIntentSource source)
        {
            RallySession session = NewSession(11);
            session.Agent(PlayerSlot.Far).IntentSource = new PartnerBot(new System.Random(3));

            var nearBot = new PartnerBot(new System.Random(4));
            PlayerAgent near = session.Agent(PlayerSlot.Near);
            near.IntentSource = nearBot;

            // Bots serve and return until the far player has just played a ball that lands deep in the near court.
            for (int i = 0; i < (int)(120f / Dt); i++)
            {
                session.Tick(Dt);
                foreach (RallyEvent e in session.Events)
                {
                    if (e.Type != RallyEventType.Hit || e.Slot != PlayerSlot.Far) continue;
                    Vector3 landing = session.Trajectory.FirstBouncePoint;
                    bool deep = session.Trajectory.HasFirstBounce && landing.z < -8f && landing.z > -CourtGeometry.HalfLength && Mathf.Abs(landing.x) < 3f;
                    if (deep && near.Position.z < -9f && e.Shot.Kind == ShotKind.Topspin)
                    {
                        near.IntentSource = source;
                        return (session, near);
                    }
                }
            }
            Assert.Fail("No deep ball was played.");
            return default;
        }

        [Test]
        public void Stick_MovesThePlayerForwardWhileAShotIsPrepared()
        {
            var script = new StickWhilePreparing { Move = new Vector2(0f, 1f) };
            (RallySession session, PlayerAgent near) = IncomingDeepBall(script);
            float startZ = near.Position.z;

            float bestZ = startZ;
            bool hit = false;
            for (int i = 0; i < (int)(1.6f / Dt) && !hit; i++)
            {
                session.Tick(Dt);
                bestZ = Mathf.Max(bestZ, near.Position.z);
                foreach (RallyEvent e in session.Events)
                    if (e.Type == RallyEventType.Hit && e.Slot == PlayerSlot.Near) hit = true;
            }

            TestContext.WriteLine($"z from {startZ:F2} to {bestZ:F2}, hit {hit}");
            Assert.That(bestZ - startZ, Is.GreaterThan(1.5f), "the stick must keep moving the player while the button is held");
            Assert.That(hit, Is.True, "moving in must not lose the ball: the contact point follows the player");
        }

        [Test]
        public void NeutralStick_StillRunsToTheBallWhileAShotIsPrepared()
        {
            var script = new StickWhilePreparing();
            (RallySession session, PlayerAgent near) = IncomingDeepBall(script);

            bool hit = false;
            for (int i = 0; i < (int)(2f / Dt) && !hit; i++)
            {
                session.Tick(Dt);
                foreach (RallyEvent e in session.Events)
                    if (e.Type == RallyEventType.Hit && e.Slot == PlayerSlot.Near) hit = true;
            }
            Assert.That(hit, Is.True);
        }

        [Test]
        public void Stick_IsFreeBeforeAnyButtonIsPressed()
        {
            RallySession session = NewSession(5);
            PlayerAgent near = session.Agent(PlayerSlot.Near);
            session.Agent(PlayerSlot.Far).IntentSource = new PartnerBot(new System.Random(1));
            near.IntentSource = new StickWhilePreparing { Move = new Vector2(0f, 1f) };
            near.Movement.Teleport(new Vector3(0f, 0f, -12f));

            // The receiver may walk during the serve; the far player serves on the second point in real play.
            for (int i = 0; i < (int)(1f / Dt); i++) near.Movement.Tick(new Vector3(0f, 0f, 6.2f), Dt);
            Assert.That(near.Position.z, Is.GreaterThan(-7f), "nothing about the court holds a player behind the baseline");
        }

        [Test]
        public void Planner_ForcedBackhandStandsOnTheOtherSideOfTheBall()
        {
            var tuning = new ShotTuning();
            var movement = new PlayerMovementTuning();
            var state = new BallState { Position = new Vector3(1f, 1.2f, 6f), Velocity = new Vector3(0f, 3f, -24f), Spin = Vector3.right * -200f };
            var trajectory = new BallTrajectory();
            trajectory.Compute(state, 0f, BallPhysicsParams.Default, Dt, 3f);
            Vector3 feet = new Vector3(0f, 0f, -12f);

            ContactPlan fore = ContactPlanner.Find(trajectory, feet, PlayerSlot.Near, HandSide.Forehand, ShotButton.Forehand, 0f, 7f, movement, tuning);
            ContactPlan back = ContactPlanner.Find(trajectory, feet, PlayerSlot.Near, HandSide.Backhand, ShotButton.Backhand, 0f, 7f, movement, tuning);

            Assert.That(fore.Side, Is.EqualTo(HandSide.Forehand));
            Assert.That(back.Side, Is.EqualTo(HandSide.Backhand));
            Assert.That(fore.Stance.x, Is.LessThan(fore.BallPoint.x), "near player's right is +X, so the ball is to the right of the feet");
            Assert.That(back.Stance.x, Is.GreaterThan(back.BallPoint.x));
        }
    }
}
