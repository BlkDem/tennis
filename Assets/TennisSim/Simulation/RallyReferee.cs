using UnityEngine;

namespace TennisSim.Simulation
{
    public enum PointEndReason { None, Out, Net, DoubleBounce, OwnSide, DoubleFault, Ace }

    /// <summary>A serve that does not start a rally: a fault, or a let that touched the net cord and is replayed.</summary>
    public enum ServeCall { None, Fault, Let }

    /// <summary>Applies the rules of a rally to hits, bounces and net contacts. Reports the moment a point or a serve is decided.</summary>
    public sealed class RallyReferee
    {
        enum State { Idle, Serve, Live, Finished, Called }

        State state = State.Idle;
        PlayerSlot lastHitter;
        ServiceBox box;
        bool netTouched;
        bool cordTouched;

        public int BouncesSinceHit { get; private set; }
        public int Hits { get; private set; }
        public bool IsFinished => state == State.Finished;
        /// <summary>The serve is in the air and has not landed yet.</summary>
        public bool ServeInFlight => state == State.Serve;
        public PlayerSlot Winner { get; private set; }
        public PointEndReason Reason { get; private set; }
        /// <summary>Set when the serve was a fault or a let; the session then decides what follows.</summary>
        public ServeCall Call { get; private set; }

        /// <summary>The server has struck the ball. It must land in <paramref name="serviceBox"/> before anyone may play it.</summary>
        public void BeginServe(PlayerSlot server, ServiceBox serviceBox)
        {
            state = State.Serve;
            lastHitter = server;
            box = serviceBox;
            netTouched = false;
            cordTouched = false;
            BouncesSinceHit = 0;
            Hits = 1;
            Reason = PointEndReason.None;
            Call = ServeCall.None;
        }

        /// <summary>The receiver may get ready during the serve, but only strikes it after the bounce.</summary>
        public bool CanHit(PlayerSlot slot) =>
            (state == State.Serve || state == State.Live) && slot != lastHitter && BouncesSinceHit < 2;

        public void OnHit(PlayerSlot slot)
        {
            state = State.Live;
            lastHitter = slot;
            netTouched = false;
            BouncesSinceHit = 0;
            Hits++;
        }

        public void OnNetFabric()
        {
            if (state == State.Live) netTouched = true;
        }

        public void OnNetCord()
        {
            if (state == State.Serve) cordTouched = true;
        }

        public void OnGround(Vector3 point)
        {
            if (state == State.Serve)
            {
                if (!box.Contains(point.x, point.z)) Call = ServeCall.Fault;
                else if (cordTouched) Call = ServeCall.Let;
                else
                {
                    state = State.Live;
                    BouncesSinceHit = 1;
                    return;
                }
                state = State.Called;
                return;
            }
            if (state != State.Live) return;

            BouncesSinceHit++;
            PlayerSlot opponent = CourtGeometry.Opponent(lastHitter);

            if (BouncesSinceHit >= 2)
            {
                Finish(lastHitter, Hits == 1 ? PointEndReason.Ace : PointEndReason.DoubleBounce);
                return;
            }

            if (CourtGeometry.HalfOwner(point.z) == lastHitter)
                Finish(opponent, netTouched ? PointEndReason.Net : PointEndReason.OwnSide);
            else if (!CourtGeometry.IsInsideSingles(point.x, point.z))
                Finish(opponent, PointEndReason.Out);
        }

        void Finish(PlayerSlot winner, PointEndReason reason)
        {
            state = State.Finished;
            Winner = winner;
            Reason = reason;
        }
    }
}
