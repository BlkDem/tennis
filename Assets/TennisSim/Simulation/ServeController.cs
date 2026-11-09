using UnityEngine;
using Random = System.Random;

namespace TennisSim.Simulation
{
    public enum ServePhase { Idle, Tossing, Swinging, Done }

    /// <summary>
    /// The serve, with the same rhythm as every other shot: press tosses the ball, release swings, and the
    /// racket arrives <see cref="ShotTuning.SwingLatency"/> later. Striking the ball as it drops through
    /// the contact height gives the fastest, most accurate serve. Holding the button swings late by itself.
    /// The button picks the serve: flat is fast and risky, slice curves away, anything else is a safe kick.
    /// </summary>
    public sealed class ServeController
    {
        readonly PlayerSlot slot;
        readonly ShotTuning tuning;
        readonly BallPhysicsParams physics;
        readonly Random rng;

        float swingStart;

        public ServePhase Phase { get; private set; }
        public ShotButton Button { get; private set; }
        /// <summary>The player pressed this tick and the session should toss the ball.</summary>
        public bool TossRequested { get; private set; }
        public float SwingStart => swingStart;

        public ServeController(PlayerSlot slot, ShotTuning tuning, in BallPhysicsParams physics, Random rng)
        {
            this.slot = slot;
            this.tuning = tuning;
            this.physics = physics;
            this.rng = rng;
        }

        public static ShotKind KindFor(ShotButton button) =>
            button == ShotButton.Flat ? ShotKind.ServeFlat
            : button == ShotButton.Slice ? ShotKind.ServeSlice
            : ShotKind.ServeKick;

        public void Reset()
        {
            Phase = ServePhase.Idle;
            TossRequested = false;
        }

        /// <param name="target">A point in the box picked with the aim cursor, world X and Z; replaces <paramref name="aim"/> when set.</param>
        public ShotResult? Tick(in PlayerPerception p, in PlayerIntent intent, Vector2 aim, Vector2? target = null)
        {
            TossRequested = false;
            switch (Phase)
            {
                case ServePhase.Idle:
                    if (p.AwaitingServe && p.IsServer && intent.Pressed.HasValue)
                    {
                        Button = intent.Pressed.Value;
                        Phase = ServePhase.Tossing;
                        TossRequested = true;
                    }
                    break;

                case ServePhase.Tossing:
                    if (!p.Tossing) break;
                    bool released = intent.Released.HasValue && intent.Released.Value == Button;
                    bool heldTooLong = !float.IsNaN(p.ServeContactTime)
                        && p.Now >= p.ServeContactTime - tuning.SwingLatency + tuning.AutoSwingLateness;
                    if (released || heldTooLong)
                    {
                        swingStart = p.Now;
                        Phase = ServePhase.Swinging;
                    }
                    break;

                case ServePhase.Swinging:
                    float sweetTime = swingStart + tuning.SwingLatency;
                    if (p.Now < sweetTime) break;
                    Phase = ServePhase.Done;
                    if (!p.Tossing || p.Ball.Position.y < tuning.TossCatchHeight) return null;
                    return Strike(p, sweetTime, aim, target);
            }
            return null;
        }

        /// <param name="aim">Read at the moment of contact, so the player can steer the serve during the toss.</param>
        ShotResult Strike(in PlayerPerception p, float sweetTime, Vector2 aim, Vector2? target)
        {
            float timingError = float.IsNaN(p.ServeContactTime) ? 0f : sweetTime - p.ServeContactTime;
            ContactEvaluation evaluation = ContactEvaluator.Evaluate(timingError, 0f, 0f, tuning.ServeTimingWindow, tuning);
            return ShotPlanner.PlanServe(KindFor(Button), slot, p.Ball.Position, p.ServeBox, aim, evaluation, tuning, physics, rng, target);
        }
    }
}
