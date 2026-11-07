using System;
using System.Collections.Generic;
using UnityEngine;

namespace TennisSim.Simulation
{
    public enum SessionState { AwaitingServe, Rally, PointOver }

    public enum RallyEventType { ServeDropped, Hit, Bounce, NetFabric, NetCord, PointEnded, ServeReset }

    public struct RallyEvent
    {
        public RallyEventType Type;
        public PlayerSlot Slot;
        public Vector3 Point;
        public float ImpactSpeed;
        public ShotResult Shot;
        public PointEndReason Reason;
    }

    /// <summary>
    /// The whole gameplay loop as plain C#: ball, two players, referee. It runs identically in the game
    /// and in headless tests. The MonoBehaviour layer only feeds intents in and presents the result.
    /// </summary>
    public sealed class RallySession
    {
        public const float TickRate = 120f;
        public const float TickDt = 1f / TickRate;
        const float PredictionHorizon = 5f;
        const float PointOverDuration = 2.2f;
        const float ServeHeight = 1.6f;
        const float ServerBehindBaseline = 0.4f;
        const float ServerOffsetX = 1.2f;
        const float ReceiverBehindBaseline = 0.8f;
        const float ReceiverOffsetX = 2f;

        readonly BallPhysicsParams physics;
        readonly ShotTuning shotTuning;
        readonly RallyReferee referee = new RallyReferee();
        readonly PlayerAgent[] agents;
        readonly List<RallyEvent> events = new List<RallyEvent>();
        readonly BallTrajectory trajectory = new BallTrajectory();
        readonly int[] points = new int[2];

        BallState ball;
        float pointOverAt;

        public float Time { get; private set; }
        public SessionState State { get; private set; }
        public PlayerSlot Server { get; private set; } = PlayerSlot.Near;
        public BallState Ball => ball;
        public BallTrajectory Trajectory => trajectory;
        public int BallRevision { get; private set; }
        public int RallyHits => referee.Hits;
        public int BouncesSinceHit => referee.BouncesSinceHit;
        public IReadOnlyList<RallyEvent> Events => events;

        public RallySession(in BallPhysicsParams physics, ShotTuning shotTuning, PlayerMovementTuning movementTuning, int seed)
        {
            this.physics = physics;
            this.shotTuning = shotTuning;
            var rng = new System.Random(seed);
            agents = new[]
            {
                new PlayerAgent(PlayerSlot.Near, movementTuning, shotTuning, physics, rng),
                new PlayerAgent(PlayerSlot.Far, movementTuning, shotTuning, physics, rng),
            };
            StartPoint(PlayerSlot.Near);
        }

        public PlayerAgent Agent(PlayerSlot slot) => agents[(int)slot];
        public int Points(PlayerSlot slot) => points[(int)slot];

        public void Tick(float dt)
        {
            Time += dt;
            events.Clear();

            TickPlayer(agents[0], dt);
            TickPlayer(agents[1], dt);

            if (State == SessionState.AwaitingServe)
            {
                HoldBallAtServer();
                foreach (PlayerAgent agent in agents)
                    if (agent.Slot == Server && agent.ServeRequested) DropServe();
                return;
            }

            StepBall(dt);

            if (State == SessionState.Rally)
            {
                if (referee.FeedExpired)
                {
                    PlayerSlot server = Server;
                    StartPoint(server);
                    events.Add(new RallyEvent { Type = RallyEventType.ServeReset, Slot = server });
                }
                else if (referee.IsFinished)
                {
                    EndPoint();
                }
            }
            else if (State == SessionState.PointOver && Time >= pointOverAt)
            {
                StartPoint(CourtGeometry.Opponent(Server));
            }
        }

        void TickPlayer(PlayerAgent agent, float dt)
        {
            var perception = new PlayerPerception
            {
                Now = Time,
                Slot = agent.Slot,
                Position = agent.Position,
                Ball = ball,
                Trajectory = trajectory,
                BouncesSinceHit = referee.BouncesSinceHit,
                BallRevision = BallRevision,
                CanHit = State == SessionState.Rally && referee.CanHit(agent.Slot),
                AwaitingServe = State == SessionState.AwaitingServe,
                IsServer = agent.Slot == Server,
            };

            ShotResult? shot = agent.Tick(perception, dt);
            if (shot.HasValue) ApplyShot(agent.Slot, shot.Value);
        }

        void ApplyShot(PlayerSlot slot, in ShotResult shot)
        {
            ball.Velocity = shot.Velocity;
            ball.Spin = shot.Spin;
            referee.OnHit(slot);
            RefreshPrediction();
            events.Add(new RallyEvent { Type = RallyEventType.Hit, Slot = slot, Point = ball.Position, Shot = shot });
        }

        void StepBall(float dt)
        {
            BallContact contact = BallSimulator.Step(ref ball, physics, dt);
            switch (contact.Type)
            {
                case BallContactType.Ground:
                    if (State == SessionState.Rally) referee.OnGround(contact.Point);
                    events.Add(new RallyEvent { Type = RallyEventType.Bounce, Point = contact.Point, ImpactSpeed = contact.ImpactSpeed });
                    break;
                case BallContactType.Net:
                    if (State == SessionState.Rally) referee.OnNetFabric();
                    events.Add(new RallyEvent { Type = RallyEventType.NetFabric, Point = contact.Point, ImpactSpeed = contact.ImpactSpeed });
                    break;
                case BallContactType.NetCord:
                    events.Add(new RallyEvent { Type = RallyEventType.NetCord, Point = contact.Point, ImpactSpeed = contact.ImpactSpeed });
                    break;
            }
        }

        void EndPoint()
        {
            State = SessionState.PointOver;
            pointOverAt = Time + PointOverDuration;
            points[(int)referee.Winner]++;
            trajectory.Clear();
            events.Add(new RallyEvent { Type = RallyEventType.PointEnded, Slot = referee.Winner, Reason = referee.Reason });
        }

        void StartPoint(PlayerSlot server)
        {
            Server = server;
            State = SessionState.AwaitingServe;
            trajectory.Clear();
            BallRevision++;

            float serverForward = CourtGeometry.ForwardSign(server);
            float serverSide = -serverForward;
            float serverZ = -serverForward * (CourtGeometry.HalfLength + ServerBehindBaseline);
            agents[(int)server].ResetForPoint(new Vector3(serverSide * ServerOffsetX, 0f, serverZ));

            PlayerSlot receiver = CourtGeometry.Opponent(server);
            float receiverZ = -CourtGeometry.ForwardSign(receiver) * (CourtGeometry.HalfLength + ReceiverBehindBaseline);
            agents[(int)receiver].ResetForPoint(new Vector3(serverSide * ReceiverOffsetX, 0f, receiverZ));

            HoldBallAtServer();
        }

        /// <summary>In the air at the server's forehand, where the drop will start.</summary>
        void HoldBallAtServer()
        {
            PlayerAgent agent = agents[(int)Server];
            Vector3 offset = StrikeZone.Offset(Server, HandSide.Forehand, ServeHeight, shotTuning);
            ball = new BallState { Position = agent.Position + offset + Vector3.up * ServeHeight };
        }

        void DropServe()
        {
            State = SessionState.Rally;
            referee.BeginFeed(Server);
            RefreshPrediction();
            events.Add(new RallyEvent { Type = RallyEventType.ServeDropped, Slot = Server, Point = ball.Position });
        }

        void RefreshPrediction()
        {
            BallRevision++;
            trajectory.Compute(ball, Time, physics, TickDt, PredictionHorizon);
        }
    }
}
