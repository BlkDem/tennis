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
        public BallState Ball;
        public BallTrajectory Trajectory;
        public int BouncesSinceHit;
        /// <summary>Changes whenever the ball's trajectory changes (a new hit or serve).</summary>
        public int BallRevision;
        /// <summary>This player is allowed to play the ball right now.</summary>
        public bool CanHit;
        public bool AwaitingServe;
        public bool IsServer;
    }

    /// <summary>Source of player decisions: a human through InputManager, a bot, or a test script.</summary>
    public interface IPlayerIntentSource
    {
        PlayerIntent ReadIntent(in PlayerPerception perception);
    }
}
