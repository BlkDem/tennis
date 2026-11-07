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

        /// <summary>Near serves from the deuce side, so the serve must land in the far court's box on the -X side.</summary>
        static readonly ServiceBox NearDeuceBox = CourtGeometry.ServiceBoxFor(PlayerSlot.Near, true);
        static readonly Vector3 InNearDeuceBox = new Vector3(-2f, 0f, 4f);

        /// <summary>A good serve by Near, returned by Far: the rally is live and Far was the last to hit.</summary>
        static RallyReferee AfterServeAndReturn()
        {
            var referee = new RallyReferee();
            referee.BeginServe(PlayerSlot.Near, NearDeuceBox);
            referee.OnGround(InNearDeuceBox);
            referee.OnHit(PlayerSlot.Far);
            return referee;
        }

        [Test]
        public void ServiceBox_IsDiagonallyAcrossFromTheServer()
        {
            Assert.That(NearDeuceBox.MaxX, Is.EqualTo(0f));
            Assert.That(NearDeuceBox.MinZ, Is.EqualTo(0f));
            Assert.That(NearDeuceBox.MaxZ, Is.EqualTo(CourtGeometry.ServiceLineFromNet));
            Assert.That(CourtGeometry.ServeSideSign(PlayerSlot.Near, true), Is.EqualTo(1f), "the deuce side is the server's right");

            ServiceBox farAd = CourtGeometry.ServiceBoxFor(PlayerSlot.Far, false);
            Assert.That(farAd.MaxX, Is.EqualTo(0f), "far serving from the ad side stands at +X and serves to -X");
            Assert.That(farAd.MaxZ, Is.EqualTo(0f));
        }

        [Test]
        public void Referee_BallOutGivesThePointToTheOpponent()
        {
            RallyReferee referee = AfterServeAndReturn();
            referee.OnGround(new Vector3(0f, 0f, -13f));

            Assert.That(referee.IsFinished);
            Assert.That(referee.Winner, Is.EqualTo(PlayerSlot.Near));
            Assert.That(referee.Reason, Is.EqualTo(PointEndReason.Out));
        }

        [Test]
        public void Referee_LineIsIn()
        {
            RallyReferee referee = AfterServeAndReturn();
            referee.OnGround(new Vector3(CourtGeometry.HalfSinglesWidth, 0f, -CourtGeometry.HalfLength));
            Assert.That(referee.IsFinished, Is.False);
        }

        [Test]
        public void Referee_DoublesAlleyIsOutInSingles()
        {
            RallyReferee referee = AfterServeAndReturn();
            referee.OnGround(new Vector3(CourtGeometry.HalfSinglesWidth + 0.5f, 0f, -8f));
            Assert.That(referee.Reason, Is.EqualTo(PointEndReason.Out));
        }

        [Test]
        public void Referee_DoubleBounceGivesThePointToTheHitter()
        {
            RallyReferee referee = AfterServeAndReturn();
            referee.OnGround(new Vector3(0f, 0f, -8f));
            Assert.That(referee.CanHit(PlayerSlot.Near), Is.True);
            Assert.That(referee.CanHit(PlayerSlot.Far), Is.False);
            referee.OnGround(new Vector3(0f, 0f, -11f));

            Assert.That(referee.Winner, Is.EqualTo(PlayerSlot.Far));
            Assert.That(referee.Reason, Is.EqualTo(PointEndReason.DoubleBounce));
        }

        [Test]
        public void Referee_NetFaultLosesThePoint()
        {
            RallyReferee referee = AfterServeAndReturn();
            referee.OnNetFabric();
            referee.OnGround(new Vector3(0f, 0f, 0.3f));

            Assert.That(referee.Winner, Is.EqualTo(PlayerSlot.Near));
            Assert.That(referee.Reason, Is.EqualTo(PointEndReason.Net));
        }

        [TestCase(2f, 4f)]
        [TestCase(-2f, 7f)]
        [TestCase(-5f, 4f)]
        [TestCase(-2f, -3f)]
        public void Referee_ServeOutsideTheBoxIsAFault(float x, float z)
        {
            var referee = new RallyReferee();
            referee.BeginServe(PlayerSlot.Near, NearDeuceBox);
            referee.OnGround(new Vector3(x, 0f, z));
            Assert.That(referee.Call, Is.EqualTo(ServeCall.Fault));
            Assert.That(referee.IsFinished, Is.False, "a fault is not a point by itself");
        }

        [Test]
        public void Referee_ServeLinesAreIn()
        {
            var referee = new RallyReferee();
            referee.BeginServe(PlayerSlot.Near, NearDeuceBox);
            referee.OnGround(new Vector3(0f, 0f, CourtGeometry.ServiceLineFromNet));
            Assert.That(referee.Call, Is.EqualTo(ServeCall.None));
            Assert.That(referee.ServeInFlight, Is.False);
        }

        [Test]
        public void Referee_NetCordIntoTheBoxIsALetAndOutOfItAFault()
        {
            var let = new RallyReferee();
            let.BeginServe(PlayerSlot.Near, NearDeuceBox);
            let.OnNetCord();
            let.OnGround(InNearDeuceBox);
            Assert.That(let.Call, Is.EqualTo(ServeCall.Let));

            var fault = new RallyReferee();
            fault.BeginServe(PlayerSlot.Near, NearDeuceBox);
            fault.OnNetCord();
            fault.OnGround(new Vector3(-2f, 0f, 8f));
            Assert.That(fault.Call, Is.EqualTo(ServeCall.Fault));
        }

        [Test]
        public void Referee_AnUntouchedServeIsAnAce()
        {
            var referee = new RallyReferee();
            referee.BeginServe(PlayerSlot.Near, NearDeuceBox);
            Assert.That(referee.CanHit(PlayerSlot.Far), Is.True, "the receiver may get ready while the serve flies");
            Assert.That(referee.CanHit(PlayerSlot.Near), Is.False);
            referee.OnGround(InNearDeuceBox);
            referee.OnGround(new Vector3(-2.5f, 0f, 14f));

            Assert.That(referee.Winner, Is.EqualTo(PlayerSlot.Near));
            Assert.That(referee.Reason, Is.EqualTo(PointEndReason.Ace));
        }

        [Test]
        public void Score_CountsDeuceAdvantageAndGames()
        {
            var score = new TennisScore(PlayerSlot.Near);
            Assert.That(score.DeuceSide, Is.True);
            score.AddPoint(PlayerSlot.Near);
            Assert.That(score.DeuceSide, Is.False, "the second point is served from the ad side");

            for (int i = 0; i < 3; i++) score.AddPoint(PlayerSlot.Far);
            score.AddPoint(PlayerSlot.Near);
            score.AddPoint(PlayerSlot.Near);
            Assert.That(score.IsDeuce, Is.True);
            Assert.That(score.AddPoint(PlayerSlot.Far), Is.EqualTo(ScoreChange.Point));
            Assert.That(score.Advantage, Is.EqualTo(PlayerSlot.Far));
            score.AddPoint(PlayerSlot.Near);
            Assert.That(score.IsDeuce, Is.True);
            score.AddPoint(PlayerSlot.Far);
            Assert.That(score.AddPoint(PlayerSlot.Far), Is.EqualTo(ScoreChange.Game));

            Assert.That(score.Games(PlayerSlot.Far), Is.EqualTo(1));
            Assert.That(score.GamePoints(PlayerSlot.Near), Is.EqualTo(0));
            Assert.That(score.Server, Is.EqualTo(PlayerSlot.Far), "the serve changes hands after every game");
        }

        [Test]
        public void Score_ASetNeedsATwoGameLead()
        {
            var score = new TennisScore(PlayerSlot.Near);
            void WinGame(PlayerSlot slot)
            {
                for (int i = 0; i < 4; i++) score.AddPoint(slot);
            }

            for (int i = 0; i < 5; i++)
            {
                WinGame(PlayerSlot.Near);
                WinGame(PlayerSlot.Far);
            }
            WinGame(PlayerSlot.Near);
            Assert.That(score.Sets(PlayerSlot.Near), Is.EqualTo(0), "6:5 is not a set");
            WinGame(PlayerSlot.Near);
            Assert.That(score.Sets(PlayerSlot.Near), Is.EqualTo(1));
            Assert.That(score.Games(PlayerSlot.Near), Is.EqualTo(0));
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
            int serves = 0;
            int faults = 0;
            int lets = 0;
            int bouncesSinceServe = -1;
            var servers = new HashSet<PlayerSlot>();

            int ticks = (int)(3000f / Dt);
            for (int i = 0; i < ticks && rallyLengths.Count < 150; i++)
            {
                session.Tick(Dt);

                BallState ball = session.Ball;
                Assert.That(float.IsNaN(ball.Position.magnitude), Is.False);
                Assert.That(ball.Position.y, Is.GreaterThanOrEqualTo(BallPhysicsParams.Default.Radius - 1e-3f));

                foreach (RallyEvent e in session.Events)
                {
                    switch (e.Type)
                    {
                        case RallyEventType.Hit:
                            if (hitsThisRally == 0)
                            {
                                serves++;
                                servers.Add(e.Slot);
                                bouncesSinceServe = 0;
                            }
                            else if (bouncesSinceServe >= 0)
                            {
                                Assert.That(bouncesSinceServe, Is.GreaterThanOrEqualTo(1), "a serve must bounce before it is returned");
                                bouncesSinceServe = -1;
                            }
                            hitsThisRally++;
                            kinds[e.Shot.Kind] = kinds.TryGetValue(e.Shot.Kind, out int k) ? k + 1 : 1;
                            qualities[e.Shot.Quality] = qualities.TryGetValue(e.Shot.Quality, out int q) ? q + 1 : 1;
                            break;
                        case RallyEventType.Bounce:
                            if (bouncesSinceServe >= 0) bouncesSinceServe++;
                            break;
                        case RallyEventType.Fault:
                        case RallyEventType.Let:
                            if (e.Type == RallyEventType.Fault) faults++;
                            else lets++;
                            hitsThisRally = 0;
                            bouncesSinceServe = -1;
                            break;
                        case RallyEventType.PointEnded:
                            rallyLengths.Add(hitsThisRally);
                            hitsThisRally = 0;
                            bouncesSinceServe = -1;
                            reasons[e.Reason] = reasons.TryGetValue(e.Reason, out int r) ? r + 1 : 1;
                            break;
                    }
                }
            }

            string Show<T>(Dictionary<T, int> d) => string.Join(", ", d.Select(p => $"{p.Key}={p.Value}"));
            TestContext.WriteLine($"points {rallyLengths.Count}, mean hits {rallyLengths.Average():F1}, max {rallyLengths.Max()}");
            TestContext.WriteLine($"serves {serves}, faults {faults}, lets {lets}");
            TestContext.WriteLine($"kinds: {Show(kinds)}");
            TestContext.WriteLine($"quality: {Show(qualities)}");
            TestContext.WriteLine($"ends: {Show(reasons)}");
            TestContext.WriteLine($"points won {session.Points(PlayerSlot.Near)}:{session.Points(PlayerSlot.Far)}, games {session.Score.Games(PlayerSlot.Near)}:{session.Score.Games(PlayerSlot.Far)}, sets {session.Score.Sets(PlayerSlot.Near)}:{session.Score.Sets(PlayerSlot.Far)}");

            Assert.That(rallyLengths.Count, Is.GreaterThanOrEqualTo(100), "points must keep finishing");
            Assert.That(rallyLengths.Average(), Is.InRange(4f, 25f), "rallies should be sustained, but must end");
            Assert.That(rallyLengths.Max(), Is.GreaterThanOrEqualTo(12));
            Assert.That(session.Points(PlayerSlot.Near) + session.Points(PlayerSlot.Far), Is.EqualTo(rallyLengths.Count));
            foreach (ShotKind kind in new[] { ShotKind.Topspin, ShotKind.Slice, ShotKind.Flat, ShotKind.Lob, ShotKind.Drop, ShotKind.Smash,
                         ShotKind.ServeFlat, ShotKind.ServeSlice, ShotKind.ServeKick })
                Assert.That(kinds.ContainsKey(kind), Is.True, $"{kind} never happened");
            Assert.That(reasons.ContainsKey(PointEndReason.Out) && reasons.ContainsKey(PointEndReason.Net), Is.True, "points should end in different ways");
            Assert.That(servers.Count, Is.EqualTo(2), "the serve must change hands between games");
            Assert.That(faults, Is.GreaterThan(0), "first serves take risks and sometimes miss");
            Assert.That(faults / (float)serves, Is.LessThan(0.4f), "most serves should go in");
        }

        /// <summary>The near player serves with a chosen button, releasing at an offset from the ideal moment, or holding on.</summary>
        sealed class ScriptedServer : IPlayerIntentSource
        {
            public ShotButton Button = ShotButton.Auto;
            public float ReleaseOffset;
            public bool Hold;
            public Vector2 Aim;
            public Vector2 Move;
            bool held;

            public PlayerIntent ReadIntent(in PlayerPerception p)
            {
                var intent = new PlayerIntent { Aim = Aim, Move = Move };
                if (!p.IsServer) return intent;
                if (p.AwaitingServe)
                {
                    if (held)
                    {
                        intent.Released = Button;
                        held = false;
                    }
                    else if (Move == Vector2.zero)
                    {
                        intent.Pressed = Button;
                        held = true;
                    }
                    return intent;
                }
                if (p.Tossing && held && !Hold && p.Now >= p.ServeContactTime - new ShotTuning().SwingLatency + ReleaseOffset - 1e-4f)
                {
                    intent.Released = Button;
                    held = false;
                }
                return intent;
            }
        }

        /// <summary>The near player's first serve, and whether it was a fault.</summary>
        static (ShotResult serve, bool fault) FirstServe(ScriptedServer server, int seed, ShotTuning tuning = null)
        {
            var session = new RallySession(BallPhysicsParams.Default, tuning ?? new ShotTuning(), new PlayerMovementTuning(), seed);
            session.Agent(PlayerSlot.Near).IntentSource = server;
            session.Agent(PlayerSlot.Far).IntentSource = new PartnerBot(new System.Random(seed));

            ShotResult? serve = null;
            for (int i = 0; i < (int)(8f / Dt); i++)
            {
                session.Tick(Dt);
                bool landed = false;
                bool called = false;
                foreach (RallyEvent e in session.Events)
                {
                    if (e.Type == RallyEventType.Hit && e.Slot == PlayerSlot.Near && !serve.HasValue) serve = e.Shot;
                    if (serve.HasValue && e.Type == RallyEventType.Bounce) landed = true;
                    if (serve.HasValue && e.Type == RallyEventType.Fault) called = true;
                }
                if (called || landed) return (serve.Value, called);
            }
            Assert.Fail("The serve never landed.");
            return default;
        }

        [Test]
        public void Serve_ReleaseTimingDecidesTheQuality()
        {
            Assert.That(FirstServe(new ScriptedServer { ReleaseOffset = 0f }, 1).serve.Quality, Is.EqualTo(ContactQuality.Perfect));
            Assert.That(FirstServe(new ScriptedServer { Hold = true }, 1).serve.Quality, Is.EqualTo(ContactQuality.Good), "holding swings late by itself");
            Assert.That(FirstServe(new ScriptedServer { ReleaseOffset = -0.2f }, 1).serve.Quality, Is.EqualTo(ContactQuality.Poor));

            ShotResult early = FirstServe(new ScriptedServer { ReleaseOffset = -0.2f }, 1).serve;
            Assert.That(early.TimingError, Is.LessThan(-0.15f));
        }

        [TestCase(ShotButton.Auto, ShotKind.ServeKick, 0.9f)]
        [TestCase(ShotButton.Slice, ShotKind.ServeSlice, 0.85f)]
        [TestCase(ShotButton.Flat, ShotKind.ServeFlat, 0.7f)]
        public void Serve_WellTimedServesLandInTheBox(ShotButton button, ShotKind kind, float minInRate)
        {
            int inside = 0;
            const int Seeds = 24;
            for (int seed = 1; seed <= Seeds; seed++)
            {
                var aim = new Vector2(seed % 3 - 1, (seed / 3) % 3 - 1);
                (ShotResult serve, bool fault) = FirstServe(new ScriptedServer { Button = button, Aim = aim }, seed);
                Assert.That(serve.Kind, Is.EqualTo(kind));
                if (!fault) inside++;
            }
            TestContext.WriteLine($"{kind}: {inside}/{Seeds} in");
            Assert.That(inside / (float)Seeds, Is.GreaterThanOrEqualTo(minInRate));
        }

        [Test]
        public void Serve_FlatIsFastestAndKickSafest()
        {
            float flat = FirstServe(new ScriptedServer { Button = ShotButton.Flat }, 3).serve.Velocity.magnitude;
            float slice = FirstServe(new ScriptedServer { Button = ShotButton.Slice }, 3).serve.Velocity.magnitude;
            float kick = FirstServe(new ScriptedServer { Button = ShotButton.Auto }, 3).serve.Velocity.magnitude;
            TestContext.WriteLine($"speeds km/h: flat {flat * 3.6f:F0}, slice {slice * 3.6f:F0}, kick {kick * 3.6f:F0}");
            Assert.That(flat, Is.GreaterThan(slice));
            Assert.That(slice, Is.GreaterThan(kick));
            Assert.That(flat * 3.6f, Is.GreaterThan(150f), "a good flat serve is a real weapon");
        }

        [Test]
        public void Serve_TwoFaultsLoseThePoint()
        {
            // With wild errors on a mistimed serve, every serve misses the box.
            var tuning = new ShotTuning { PoorError = new QualityError { LateralSigma = 25f, DepthSigma = 25f, ElevationSigma = 0f } };
            var session = new RallySession(BallPhysicsParams.Default, tuning, new PlayerMovementTuning(), 4);
            session.Agent(PlayerSlot.Near).IntentSource = new ScriptedServer { Button = ShotButton.Flat, ReleaseOffset = -0.3f };
            session.Agent(PlayerSlot.Far).IntentSource = new PartnerBot(new System.Random(4));

            var faults = new List<int>();
            RallyEvent? ended = null;
            for (int i = 0; i < (int)(15f / Dt) && !ended.HasValue; i++)
            {
                session.Tick(Dt);
                foreach (RallyEvent e in session.Events)
                {
                    if (e.Type == RallyEventType.Fault) faults.Add(e.ServeNumber);
                    if (e.Type == RallyEventType.PointEnded) ended = e;
                }
            }

            Assert.That(ended.HasValue, Is.True);
            Assert.That(faults, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(ended.Value.Reason, Is.EqualTo(PointEndReason.DoubleFault));
            Assert.That(ended.Value.Slot, Is.EqualTo(PlayerSlot.Far));
            Assert.That(session.Score.DeuceSide, Is.False, "the next point is served from the ad side");
        }

        [Test]
        public void Serve_ServerWalksOnlyAlongTheirHalfOfTheBaseline()
        {
            RallySession session = NewSession(6);
            PlayerAgent near = session.Agent(PlayerSlot.Near);
            var script = new ScriptedServer { Move = new Vector2(-1f, 0.5f) };
            near.IntentSource = script;
            session.Agent(PlayerSlot.Far).IntentSource = new PartnerBot(new System.Random(6));
            float z = near.Position.z;

            for (int i = 0; i < (int)(3f / Dt); i++) session.Tick(Dt);
            Assert.That(session.State, Is.EqualTo(SessionState.AwaitingServe));
            Assert.That(near.Position.x, Is.GreaterThan(0.2f), "the deuce-side server stays right of the centre mark");
            Assert.That(near.Position.z, Is.EqualTo(z).Within(1e-4f), "and behind the baseline");

            script.Move = new Vector2(1f, 0f);
            for (int i = 0; i < (int)(3f / Dt); i++) session.Tick(Dt);
            Assert.That(near.Position.x, Is.LessThan(CourtGeometry.HalfSinglesWidth));
            Assert.That(near.Position.x, Is.GreaterThan(3f));
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

            bool serving;

            public PlayerIntent ReadIntent(in PlayerPerception p)
            {
                var intent = new PlayerIntent { Move = Move, Sprint = true };
                if (p.IsServer && (p.AwaitingServe || p.Tossing))
                {
                    // Toss and hold: the serve swings by itself.
                    if (p.AwaitingServe && !serving)
                    {
                        intent.Pressed = ShotButton.Auto;
                        serving = true;
                    }
                    return intent;
                }
                if (serving)
                {
                    intent.Released = ShotButton.Auto;
                    serving = false;
                    return intent;
                }
                if (p.AwaitingServe)
                {
                    held = false;
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

            // The near player serves; the first ball they meet after the far player's return is a true incoming shot.
            for (int i = 0; i < (int)(60f / Dt); i++)
            {
                session.Tick(Dt);
                foreach (RallyEvent e in session.Events)
                {
                    if (e.Type == RallyEventType.Hit && e.Slot == PlayerSlot.Near && session.RallyHits >= 2)
                        return e;
                }
            }
            return null;
        }

        /// <summary>
        /// The median, because a few returns are taken on the run and lose score for movement whatever the timing;
        /// this test is about timing alone.
        /// </summary>
        static float MedianScore(float offset, bool hold)
        {
            var scores = new List<float>();
            var details = new List<string>();
            for (int seed = 1; seed <= 12; seed++)
            {
                RallyEvent? e = FirstReturn(new TimedSwinger { ReleaseOffset = offset, HoldThrough = hold }, seed);
                if (e.HasValue)
                {
                    scores.Add(e.Value.Shot.Score);
                    details.Add($"{e.Value.Shot.Score:F2}/{e.Value.Shot.TimingError:+0.00;-0.00}");
                }
            }
            TestContext.WriteLine($"offset {offset}, hold {hold}, score/timing: {string.Join(" ", details)}");
            Assert.That(scores.Count, Is.GreaterThan(0), $"no contact with offset {offset}");
            scores.Sort();
            return scores[scores.Count / 2];
        }

        [Test]
        public void Timing_PreciseReleaseBeatsHoldingAndBadTimingIsPunished()
        {
            float precise = MedianScore(0f, false);
            float held = MedianScore(0f, true);
            float early = MedianScore(-0.1f, false);
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
