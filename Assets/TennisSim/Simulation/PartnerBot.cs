using System;
using UnityEngine;
using Random = System.Random;

namespace TennisSim.Simulation
{
    /// <summary>
    /// A hitting partner for testing mechanics alone. It plays through the same intents as a human:
    /// it holds a shot button for an incoming ball and leaves balls that are going out.
    /// This is not the match AI, which is a later stage.
    /// </summary>
    public sealed class PartnerBot : IPlayerIntentSource
    {
        const float ServeDelay = 1f;
        const float ReactionTime = 0.12f;

        readonly Random rng;
        ShotButton? held;
        Vector2 aim;
        int reactedRevision = -1;
        float revisionSeenAt;
        int seenRevision = -1;
        float serveWaitStart = -1f;

        public PartnerBot(Random rng) => this.rng = rng;

        public PlayerIntent ReadIntent(in PlayerPerception p)
        {
            var intent = new PlayerIntent { Sprint = true, Aim = aim };

            if (p.AwaitingServe)
            {
                if (held.HasValue)
                {
                    intent.Released = held;
                    held = null;
                }
                if (p.IsServer) TickServe(p, ref intent);
                return intent;
            }

            serveWaitStart = -1f;

            if (held.HasValue && !p.CanHit)
            {
                intent.Released = held;
                held = null;
                return intent;
            }

            if (p.BallRevision != seenRevision)
            {
                seenRevision = p.BallRevision;
                revisionSeenAt = p.Now;
            }

            bool reacted = p.Now - revisionSeenAt >= ReactionTime;
            if (!held.HasValue && p.CanHit && reacted && reactedRevision != p.BallRevision && !BallIsGoingOut(p))
            {
                reactedRevision = p.BallRevision;
                Press(ChooseButton(), ref intent);
            }
            return intent;
        }

        void TickServe(in PlayerPerception p, ref PlayerIntent intent)
        {
            if (serveWaitStart < 0f) serveWaitStart = p.Now;
            if (p.Now - serveWaitStart < ServeDelay) return;
            reactedRevision = p.BallRevision + 1; // The serve's own drop is already covered by this press.
            Press(ShotButton.Auto, ref intent);
        }

        void Press(ShotButton button, ref PlayerIntent intent)
        {
            held = button;
            aim = new Vector2((float)(rng.NextDouble() * 1.8 - 0.9), (float)(rng.NextDouble() * 1.2 - 0.6));
            intent.Aim = aim;
            intent.Pressed = button;
        }

        static bool BallIsGoingOut(in PlayerPerception p)
        {
            if (p.BouncesSinceHit > 0 || p.Trajectory == null || !p.Trajectory.HasFirstBounce) return false;
            Vector3 landing = p.Trajectory.FirstBouncePoint;
            return !CourtGeometry.IsInsideSingles(landing.x, landing.z) || CourtGeometry.HalfOwner(landing.z) != p.Slot;
        }

        ShotButton ChooseButton()
        {
            double roll = rng.NextDouble();
            if (roll < 0.6) return ShotButton.Auto;
            if (roll < 0.78) return ShotButton.Slice;
            if (roll < 0.9) return ShotButton.Flat;
            if (roll < 0.96) return ShotButton.Lob;
            return ShotButton.Drop;
        }
    }
}
