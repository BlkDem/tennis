using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim.Tests
{
    /// <summary>Whole matches between AI players, simulated headless. Each match is played once and shared by the tests.</summary>
    public class AiTests
    {
        const float Dt = RallySession.TickDt;

        sealed class MatchStats
        {
            public readonly int[] Won = new int[2];
            public readonly int[] Errors = new int[2];
            public readonly int[] Volleys = new int[2];
            public readonly int[] FirstServes = new int[2];
            public readonly int[] FirstServeFaults = new int[2];
            public readonly int[] DoubleFaults = new int[2];
            public readonly Dictionary<ShotKind, int> Kinds = new Dictionary<ShotKind, int>();
            public readonly List<int> RallyLengths = new List<int>();
            public int AimedAway;
            public int AimedJudged;
            public float CoverDistance;
            public int CoverSamples;
            public int Points => RallyLengths.Count;

            public override string ToString() =>
                $"points {Points}, won {Won[0]}:{Won[1]}, errors {Errors[0]}:{Errors[1]}, volleys {Volleys[0]}:{Volleys[1]}, " +
                $"first-serve faults {FirstServeFaults[0]}/{FirstServes[0]} : {FirstServeFaults[1]}/{FirstServes[1]}, double faults {DoubleFaults[0]}:{DoubleFaults[1]}, " +
                $"mean rally {RallyLengths.Average():F1}, aimed away {AimedAway}/{AimedJudged}, mean |x| when the opponent strikes {CoverDistance / Mathf.Max(CoverSamples, 1):F2} m, " +
                $"kinds: {string.Join(", ", Kinds.Select(k => $"{k.Key}={k.Value}"))}";
        }

        static MatchStats mediumMatch;
        static MatchStats hardVersusEasy;

        static MatchStats MediumMatch => mediumMatch ??= Play(AiLevel.Medium, AiLevel.Medium, 7, 100);
        static MatchStats HardVersusEasy => hardVersusEasy ??= Play(AiLevel.Hard, AiLevel.Easy, 11, 80);

        static MatchStats Play(AiLevel nearLevel, AiLevel farLevel, int seed, int points)
        {
            var session = new RallySession(BallPhysicsParams.Default, new ShotTuning(), new PlayerMovementTuning(), seed);
            PlayerAgent near = session.Agent(PlayerSlot.Near);
            PlayerAgent far = session.Agent(PlayerSlot.Far);
            near.IntentSource = new OpponentAI(near, nearLevel, new System.Random(seed * 31 + 1));
            far.IntentSource = new OpponentAI(far, farLevel, new System.Random(seed * 31 + 2));

            var stats = new MatchStats();
            int hits = 0;
            bool firstServePending = false;

            for (int i = 0; i < (int)(4000f / Dt) && stats.Points < points; i++)
            {
                session.Tick(Dt);
                foreach (RallyEvent e in session.Events)
                {
                    int s = (int)e.Slot;
                    switch (e.Type)
                    {
                        case RallyEventType.Hit:
                            hits++;
                            stats.Kinds[e.Shot.Kind] = stats.Kinds.TryGetValue(e.Shot.Kind, out int k) ? k + 1 : 1;
                            if (session.RallyHits == 1)
                            {
                                firstServePending = e.ServeNumber == 1;
                                if (firstServePending) stats.FirstServes[s]++;
                                break;
                            }
                            if (e.Shot.Kind == ShotKind.Volley) stats.Volleys[s]++;

                            PlayerAgent opponent = session.Agent(CourtGeometry.Opponent(e.Slot));
                            if (Mathf.Abs(opponent.Position.x) > 1f && e.Shot.Kind != ShotKind.Lob && e.Shot.Kind != ShotKind.Defensive)
                            {
                                stats.AimedJudged++;
                                if (Mathf.Sign(e.Shot.AimedTarget.x) != Mathf.Sign(opponent.Position.x)) stats.AimedAway++;
                            }
                            stats.CoverDistance += Mathf.Abs(opponent.Position.x);
                            stats.CoverSamples++;
                            break;
                        case RallyEventType.Fault:
                            if (e.ServeNumber == 1) stats.FirstServeFaults[s]++;
                            hits = 0;
                            break;
                        case RallyEventType.Let:
                            hits = 0;
                            break;
                        case RallyEventType.PointEnded:
                            stats.Won[s]++;
                            int loser = 1 - s;
                            if (e.Reason == PointEndReason.Out || e.Reason == PointEndReason.Net || e.Reason == PointEndReason.OwnSide)
                                stats.Errors[loser]++;
                            if (e.Reason == PointEndReason.DoubleFault) stats.DoubleFaults[loser]++;
                            stats.RallyLengths.Add(hits);
                            hits = 0;
                            break;
                    }
                }
            }
            TestContext.WriteLine($"{nearLevel} vs {farLevel}: {stats}");
            return stats;
        }

        [Test]
        public void Ai_PlaysCompletePointsWithRealRallies()
        {
            MatchStats stats = MediumMatch;
            TestContext.WriteLine(stats);
            Assert.That(stats.Points, Is.EqualTo(100), "points must keep finishing");
            Assert.That(stats.RallyLengths.Average(), Is.InRange(3f, 25f));
            Assert.That(stats.RallyLengths.Max(), Is.GreaterThanOrEqualTo(10));
        }

        [Test]
        public void Ai_HitsAwayFromItsOpponent()
        {
            MatchStats stats = MediumMatch;
            TestContext.WriteLine(stats);
            Assert.That(stats.AimedJudged, Is.GreaterThan(50));
            Assert.That(stats.AimedAway / (float)stats.AimedJudged, Is.GreaterThan(0.6f));
        }

        [Test]
        public void Ai_CoversTheMiddleBetweenShots()
        {
            MatchStats stats = MediumMatch;
            TestContext.WriteLine(stats);
            Assert.That(stats.CoverDistance / stats.CoverSamples, Is.LessThan(2.2f), "when the ball is struck back, the AI should not be stranded out wide");
        }

        [Test]
        public void Ai_ServesMostlyIn()
        {
            MatchStats stats = MediumMatch;
            TestContext.WriteLine(stats);
            int firstServes = stats.FirstServes.Sum();
            Assert.That(firstServes, Is.GreaterThan(50));
            Assert.That(stats.FirstServeFaults.Sum() / (float)firstServes, Is.InRange(0.03f, 0.45f), "first serves take some risk");
            Assert.That(stats.DoubleFaults.Sum() / (float)stats.Points, Is.LessThan(0.12f));
            foreach (ShotKind kind in new[] { ShotKind.ServeFlat, ShotKind.ServeSlice, ShotKind.ServeKick })
                Assert.That(stats.Kinds.ContainsKey(kind), Is.True, $"{kind} never served");
        }

        [Test]
        public void Ai_HardBeatsEasy()
        {
            MatchStats stats = HardVersusEasy;
            TestContext.WriteLine(stats);
            Assert.That(stats.Won[0] / (float)stats.Points, Is.GreaterThan(0.62f));
            Assert.That(stats.Errors[0], Is.LessThan(stats.Errors[1]), "the easy AI misses more");
        }

        [Test]
        public void Ai_ComesToTheNetAndVolleys()
        {
            MatchStats stats = HardVersusEasy;
            TestContext.WriteLine(stats);
            Assert.That(stats.Volleys[0], Is.GreaterThan(0));
        }
    }
}
