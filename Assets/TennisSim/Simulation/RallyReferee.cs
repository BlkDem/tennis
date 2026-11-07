using UnityEngine;

namespace TennisSim.Simulation
{
    public enum PointEndReason { None, Out, Net, DoubleBounce, OwnSide }

    /// <summary>Applies the rules of a rally to hits, bounces and net contacts. Reports the moment a point is decided.</summary>
    public sealed class RallyReferee
    {
        enum State { Idle, Feeding, Live, Finished }

        State state = State.Idle;
        PlayerSlot lastHitter;
        bool netTouched;

        public int BouncesSinceHit { get; private set; }
        public int Hits { get; private set; }
        public bool IsFinished => state == State.Finished;
        public PlayerSlot Winner { get; private set; }
        public PointEndReason Reason { get; private set; }
        /// <summary>The server let the dropped ball bounce twice: no point is played, serve again.</summary>
        public bool FeedExpired { get; private set; }

        public void BeginFeed(PlayerSlot server)
        {
            state = State.Feeding;
            lastHitter = CourtGeometry.Opponent(server); // Only the server may strike the dropped ball.
            netTouched = false;
            BouncesSinceHit = 0;
            Hits = 0;
            Reason = PointEndReason.None;
            FeedExpired = false;
        }

        public bool CanHit(PlayerSlot slot) =>
            (state == State.Feeding || state == State.Live) && slot != lastHitter && BouncesSinceHit < 2;

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

        public void OnGround(Vector3 point)
        {
            if (state == State.Feeding)
            {
                BouncesSinceHit++;
                if (BouncesSinceHit >= 2) FeedExpired = true;
                return;
            }
            if (state != State.Live) return;

            BouncesSinceHit++;
            PlayerSlot opponent = CourtGeometry.Opponent(lastHitter);

            if (BouncesSinceHit >= 2)
            {
                Finish(lastHitter, PointEndReason.DoubleBounce);
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
