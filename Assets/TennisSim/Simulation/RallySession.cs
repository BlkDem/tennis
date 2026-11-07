using System;
using System.Collections.Generic;
using UnityEngine;

namespace TennisSim.Simulation
{
    public enum SessionState { AwaitingServe, Toss, Rally, ServeCalled, PointOver, MatchOver }

    public enum RallyEventType { ServeToss, TossCaught, Hit, Bounce, NetFabric, NetCord, Fault, Let, PointEnded }

    public struct RallyEvent
    {
        public RallyEventType Type;
        public PlayerSlot Slot;
        public Vector3 Point;
        public float ImpactSpeed;
        public ShotResult Shot;
        public PointEndReason Reason;
        /// <summary>For serve events: 1 for a first serve, 2 for a second.</summary>
        public int ServeNumber;
        /// <summary>For a finished point: whether it also finished a game or a set.</summary>
        public ScoreChange ScoreChange;
    }

    /// <summary>
    /// The whole gameplay loop as plain C#: ball, two players, referee and score. It runs identically in the
    /// game and in headless tests. The MonoBehaviour layer only feeds intents in and presents the result.
    /// </summary>
    public sealed class RallySession
    {
        public const float TickRate = 120f;
        public const float TickDt = 1f / TickRate;
        const float PredictionHorizon = 5f;
        const float TossPredictionHorizon = 2.5f;
        const float PointOverDuration = 2.2f;
        const float ServeCallDuration = 1.3f;
        /// <summary>After a point the players react first, then walk to where the next one starts.</summary>
        const float WalkAfterPoint = 1.6f;
        const float WalkAfterServeCall = 0.5f;
        /// <summary>The next serve waits for both players to be in place, but never longer than this after the call.</summary>
        const float MaxBetweenPoints = 6f;
        const float InPlace = 0.35f;
        const float ServerBehindBaseline = 0.15f;
        const float ServerOffsetX = 0.9f;
        const float ServerMaxX = CourtGeometry.HalfSinglesWidth - 0.3f;
        const float ServerMinX = 0.25f;
        const float ReceiverBehindBaseline = 0.9f;
        const float ReceiverOffsetX = 2.9f;

        readonly BallPhysicsParams physics;
        readonly ShotTuning shotTuning;
        readonly RallyReferee referee = new RallyReferee();
        readonly PlayerAgent[] agents;
        readonly List<RallyEvent> events = new List<RallyEvent>();
        readonly BallTrajectory trajectory = new BallTrajectory();
        readonly int[] points = new int[2];

        BallState ball;
        float stateStartedAt;
        float stateEndsAt;
        float serveContactTime = float.NaN;

        public float Time { get; private set; }
        public SessionState State { get; private set; }
        public TennisScore Score { get; }
        public PlayerSlot Server => Score.Server;
        public PlayerSlot Receiver => CourtGeometry.Opponent(Server);
        /// <summary>1 for a first serve, 2 for a second serve.</summary>
        public int ServeNumber { get; private set; } = 1;
        public ServiceBox ServeBox { get; private set; }
        public BallState Ball => ball;
        public BallTrajectory Trajectory => trajectory;
        public int BallRevision { get; private set; }
        public int RallyHits => referee.Hits;
        public int BouncesSinceHit => referee.BouncesSinceHit;
        public bool ServeInFlight => State == SessionState.Rally && referee.ServeInFlight;
        public IReadOnlyList<RallyEvent> Events => events;

        public RallySession(in BallPhysicsParams physics, ShotTuning shotTuning, PlayerMovementTuning movementTuning, int seed)
            : this(physics, shotTuning, movementTuning, seed, MatchRules.BestOfThree) { }

        public RallySession(in BallPhysicsParams physics, ShotTuning shotTuning, PlayerMovementTuning movementTuning, int seed, MatchRules rules)
        {
            this.physics = physics;
            this.shotTuning = shotTuning;
            var rng = new System.Random(seed);
            agents = new[]
            {
                new PlayerAgent(PlayerSlot.Near, movementTuning, shotTuning, physics, rng),
                new PlayerAgent(PlayerSlot.Far, movementTuning, shotTuning, physics, rng),
            };
            Score = new TennisScore(PlayerSlot.Near, rules);
            StartPoint();
        }

        public PlayerAgent Agent(PlayerSlot slot) => agents[(int)slot];
        /// <summary>All points this player has won, across games.</summary>
        public int Points(PlayerSlot slot) => points[(int)slot];

        public void Tick(float dt)
        {
            Time += dt;
            events.Clear();

            TickPlayer(agents[0], dt);
            TickPlayer(agents[1], dt);

            switch (State)
            {
                case SessionState.AwaitingServe:
                    KeepServerBehindTheirHalfOfTheBaseline();
                    HoldBallInServersHand();
                    if (agents[(int)Server].Serve.TossRequested) Toss();
                    break;

                case SessionState.Toss:
                    StepBall(dt);
                    if (State == SessionState.Toss && ball.Position.y < shotTuning.TossCatchHeight && ball.Velocity.y < 0f)
                    {
                        events.Add(new RallyEvent { Type = RallyEventType.TossCaught, Slot = Server });
                        StartServe();
                    }
                    break;

                case SessionState.Rally:
                    StepBall(dt);
                    if (referee.Call != ServeCall.None) CallServe(referee.Call);
                    else if (referee.IsFinished) EndPoint(referee.Winner, referee.Reason);
                    break;

                case SessionState.ServeCalled:
                    StepBall(dt);
                    if (WalkToPlaces(WalkAfterServeCall)) StartServe();
                    break;

                case SessionState.PointOver:
                    StepBall(dt);
                    if (Score.MatchOver)
                    {
                        if (Time >= stateEndsAt) State = SessionState.MatchOver;
                    }
                    else if (WalkToPlaces(WalkAfterPoint))
                    {
                        StartPoint();
                    }
                    break;

                case SessionState.MatchOver:
                    StepBall(dt);
                    break;
            }
        }

        void TickPlayer(PlayerAgent agent, float dt)
        {
            bool rally = State == SessionState.Rally;
            var perception = new PlayerPerception
            {
                Now = Time,
                Slot = agent.Slot,
                Position = agent.Position,
                OpponentPosition = agents[1 - (int)agent.Slot].Position,
                Ball = ball,
                Trajectory = trajectory,
                BouncesSinceHit = referee.BouncesSinceHit,
                BallRevision = BallRevision,
                CanHit = rally && referee.CanHit(agent.Slot),
                MustLetBounce = rally && referee.ServeInFlight,
                AwaitingServe = State == SessionState.AwaitingServe,
                Tossing = State == SessionState.Toss,
                ServeContactTime = State == SessionState.Toss ? serveContactTime : float.NaN,
                IsServer = agent.Slot == Server,
                ServeBox = ServeBox,
                ServeNumber = ServeNumber,
            };

            ShotResult? shot = agent.Tick(perception, dt);
            if (shot.HasValue) ApplyShot(agent.Slot, shot.Value);
        }

        void ApplyShot(PlayerSlot slot, in ShotResult shot)
        {
            ball.Velocity = shot.Velocity;
            ball.Spin = shot.Spin;
            if (State == SessionState.Toss)
            {
                State = SessionState.Rally;
                referee.BeginServe(slot, ServeBox);
            }
            else
            {
                referee.OnHit(slot);
            }
            RefreshPrediction(PredictionHorizon);
            events.Add(new RallyEvent { Type = RallyEventType.Hit, Slot = slot, Point = ball.Position, Shot = shot, ServeNumber = ServeNumber });
        }

        void StepBall(float dt)
        {
            BallContact contact = BallSimulator.Step(ref ball, physics, dt);
            bool rally = State == SessionState.Rally;
            switch (contact.Type)
            {
                case BallContactType.Ground:
                    if (rally) referee.OnGround(contact.Point);
                    events.Add(new RallyEvent { Type = RallyEventType.Bounce, Point = contact.Point, ImpactSpeed = contact.ImpactSpeed });
                    break;
                case BallContactType.Net:
                    if (rally) referee.OnNetFabric();
                    events.Add(new RallyEvent { Type = RallyEventType.NetFabric, Point = contact.Point, ImpactSpeed = contact.ImpactSpeed });
                    break;
                case BallContactType.NetCord:
                    if (rally) referee.OnNetCord();
                    events.Add(new RallyEvent { Type = RallyEventType.NetCord, Point = contact.Point, ImpactSpeed = contact.ImpactSpeed });
                    break;
            }
        }

        void CallServe(ServeCall call)
        {
            if (call == ServeCall.Fault && ServeNumber == 2)
            {
                events.Add(new RallyEvent { Type = RallyEventType.Fault, Slot = Server, ServeNumber = 2 });
                EndPoint(Receiver, PointEndReason.DoubleFault);
                return;
            }

            events.Add(new RallyEvent { Type = call == ServeCall.Fault ? RallyEventType.Fault : RallyEventType.Let, Slot = Server, ServeNumber = ServeNumber });
            if (call == ServeCall.Fault) ServeNumber = 2;
            State = SessionState.ServeCalled;
            stateStartedAt = Time;
            stateEndsAt = Time + ServeCallDuration;
            trajectory.Clear();
        }

        void EndPoint(PlayerSlot winner, PointEndReason reason)
        {
            State = SessionState.PointOver;
            stateStartedAt = Time;
            stateEndsAt = Time + PointOverDuration;
            points[(int)winner]++;
            ScoreChange change = Score.AddPoint(winner);
            trajectory.Clear();
            events.Add(new RallyEvent { Type = RallyEventType.PointEnded, Slot = winner, Reason = reason, ScoreChange = change });
        }

        void StartPoint()
        {
            ServeNumber = 1;
            StartServe();
        }

        /// <summary>Both players to their places for a first or second serve, from the side the score says.</summary>
        void StartServe()
        {
            State = SessionState.AwaitingServe;
            trajectory.Clear();
            serveContactTime = float.NaN;
            BallRevision++;

            ServeBox = CourtGeometry.ServiceBoxFor(Server, Score.DeuceSide);
            ServePositions(out Vector3 server, out Vector3 receiver);
            agents[(int)Server].ResetForPoint(server);
            agents[(int)Receiver].ResetForPoint(receiver);

            HoldBallInServersHand();
        }

        /// <summary>Where the server and receiver stand for the next serve, from the side the score says.</summary>
        void ServePositions(out Vector3 server, out Vector3 receiver)
        {
            float side = CourtGeometry.ServeSideSign(Server, Score.DeuceSide);
            server = new Vector3(side * ServerOffsetX, 0f, -CourtGeometry.ForwardSign(Server) * (CourtGeometry.HalfLength + ServerBehindBaseline));
            receiver = new Vector3(-side * ReceiverOffsetX, 0f, -CourtGeometry.ForwardSign(Receiver) * (CourtGeometry.HalfLength + ReceiverBehindBaseline));
        }

        /// <summary>
        /// Between points, after a moment to react, both players walk to their places. True once the call has been
        /// shown long enough and both have arrived, or when waiting any longer would hold up the game.
        /// </summary>
        bool WalkToPlaces(float delay)
        {
            if (Time < stateStartedAt + delay) return false;
            ServePositions(out Vector3 server, out Vector3 receiver);
            PlayerAgent serving = agents[(int)Server];
            PlayerAgent receiving = agents[(int)Receiver];
            serving.WalkTarget = server;
            receiving.WalkTarget = receiver;
            bool arrived = Flat(serving.Position - server) < InPlace && Flat(receiving.Position - receiver) < InPlace;
            return (Time >= stateEndsAt && arrived) || Time >= stateStartedAt + MaxBetweenPoints;
        }

        static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        /// <summary>Before the toss the server may walk along the baseline, but only on their half of it.</summary>
        void KeepServerBehindTheirHalfOfTheBaseline()
        {
            float side = CourtGeometry.ServeSideSign(Server, Score.DeuceSide);
            PlayerMovement movement = agents[(int)Server].Movement;
            float x = Mathf.Clamp(movement.Position.x * side, ServerMinX, ServerMaxX) * side;
            if (!Mathf.Approximately(x, movement.Position.x)) movement.Teleport(new Vector3(x, 0f, movement.Position.z));
        }

        Vector3 ServerLocal(Vector3 local)
        {
            PlayerAgent server = agents[(int)Server];
            return server.Position + StrikeZone.Right(Server) * local.x + StrikeZone.Forward(Server) * local.y + Vector3.up * local.z;
        }

        void HoldBallInServersHand() => ball = new BallState { Position = ServerLocal(shotTuning.TossStart) };

        /// <summary>
        /// Throws the ball so it peaks at the toss apex and drops through the contact point. The prediction then
        /// gives the exact moment it passes the contact height, which both the timing grade and bots use.
        /// </summary>
        void Toss()
        {
            Vector3 start = ServerLocal(shotTuning.TossStart);
            Vector3 contact = ServerLocal(shotTuning.ServeContact);
            float g = physics.Gravity;
            float rise = Mathf.Max(shotTuning.TossApex - start.y, 0.1f);
            float fall = Mathf.Max(shotTuning.TossApex - contact.y, 0.01f);
            float up = Mathf.Sqrt(2f * g * rise);
            float flightTime = up / g + Mathf.Sqrt(2f * fall / g);
            Vector3 across = new Vector3(contact.x - start.x, 0f, contact.z - start.z) / flightTime;

            ball = new BallState { Position = start, Velocity = across + Vector3.up * up };
            State = SessionState.Toss;
            RefreshPrediction(TossPredictionHorizon);

            serveContactTime = Time + flightTime;
            foreach (TrajectorySample sample in trajectory.Samples)
            {
                if (sample.State.Velocity.y < 0f && sample.State.Position.y <= shotTuning.ServeContact.z)
                {
                    serveContactTime = sample.Time;
                    break;
                }
            }
            events.Add(new RallyEvent { Type = RallyEventType.ServeToss, Slot = Server, Point = start, ServeNumber = ServeNumber });
        }

        void RefreshPrediction(float horizon)
        {
            BallRevision++;
            trajectory.Compute(ball, Time, physics, TickDt, horizon);
        }
    }
}
