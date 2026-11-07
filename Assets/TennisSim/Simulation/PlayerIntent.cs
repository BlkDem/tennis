using UnityEngine;

namespace TennisSim.Simulation
{
    /// <summary>One tick of what a player wants to do. All directions are world-space: X across the court, Y along +Z.</summary>
    public struct PlayerIntent
    {
        public Vector2 Move;
        public Vector2 Aim;
        public bool Sprint;
        public ShotButton? Pressed;
        public ShotButton? Released;
    }

    /// <summary>Everything a player may know about the game this tick.</summary>
    public struct PlayerPerception
    {
        public float Now;
        public PlayerSlot Slot;
        public Vector3 Position;
        /// <summary>Where the other player stands: everything a human sees on screen, a bot may know too.</summary>
        public Vector3 OpponentPosition;
        public BallState Ball;
        public BallTrajectory Trajectory;
        public int BouncesSinceHit;
        /// <summary>Changes whenever the ball's trajectory changes (a new hit or serve).</summary>
        public int BallRevision;
        /// <summary>This player is allowed to play the ball right now.</summary>
        public bool CanHit;
        /// <summary>The ball is a serve that has not bounced yet: the receiver must let it land.</summary>
        public bool MustLetBounce;
        public bool AwaitingServe;
        /// <summary>The server's toss is in the air.</summary>
        public bool Tossing;
        /// <summary>When the toss drops through the ideal contact height; NaN when there is no toss.</summary>
        public float ServeContactTime;
        public bool IsServer;
        /// <summary>The box the current serve must land in.</summary>
        public ServiceBox ServeBox;
        /// <summary>1 for a first serve, 2 for a second serve.</summary>
        public int ServeNumber;
    }

    /// <summary>Source of player decisions: a human through InputManager, a bot, or a test script.</summary>
    public interface IPlayerIntentSource
    {
        PlayerIntent ReadIntent(in PlayerPerception perception);
    }
}
