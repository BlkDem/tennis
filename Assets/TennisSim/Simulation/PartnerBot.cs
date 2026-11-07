using System;
using UnityEngine;
using Random = System.Random;

namespace TennisSim.Simulation
{
    /// <summary>
    /// A hitting partner for testing mechanics alone. It plays through the same intents as a human:
    /// it tosses and releases on the serve, holds a shot button for an incoming ball and leaves balls that are going out.
    /// This is not the match AI, which is a later stage.
    /// </summary>
    public sealed class PartnerBot : IPlayerIntentSource
    {
        const float ServeDelay = 1f;
        const float ReactionTime = 0.12f;
        const float SwingLatency = 0.14f;
        /// <summary>Spread of the bot's serve release around the ideal moment, seconds.</summary>
        const float ServeTimingSpread = 0.035f;

        readonly Random rng;
        ShotButton? held;
        Vector2 aim;
        int reactedRevision = -1;
        float revisionSeenAt;
        int seenRevision = -1;
        float serveWaitStart = -1f;
        float serveReleaseOffset;

        public PartnerBot(Random rng) => this.rng = rng;

        public PlayerIntent ReadIntent(in PlayerPerception p)
        {
            var intent = new PlayerIntent { Sprint = true, Aim = aim };

            if (p.IsServer && (p.AwaitingServe || p.Tossing))
            {
                TickServe(p, ref intent);
                return intent;
            }
            serveWaitStart = -1f;

            if (held.HasValue && (!p.CanHit || p.AwaitingServe))
            {
                intent.Released = held;
                held = null;
                return intent;
            }
            if (p.AwaitingServe) return intent;

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
            if (p.AwaitingServe)
            {
                if (held.HasValue)
                {
                    intent.Released = held;
                    held = null;
                    return;
                }
                if (serveWaitStart < 0f) serveWaitStart = p.Now;
                if (p.Now - serveWaitStart < ServeDelay) return;

                serveWaitStart = -1f;
                serveReleaseOffset = ShotPlanner.Gaussian(rng) * ServeTimingSpread;
                Press(ChooseServe(p.ServeNumber), ref intent);
                return;
            }

            // The toss is up: release so the racket meets the ball as it drops through the contact height.
            if (held.HasValue && !float.IsNaN(p.ServeContactTime) && p.Now >= p.ServeContactTime - SwingLatency + serveReleaseOffset)
            {
                intent.Released = held;
                held = null;
            }
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
            if (p.MustLetBounce) return !p.ServeBox.Contains(landing.x, landing.z);
            return !CourtGeometry.IsInsideSingles(landing.x, landing.z) || CourtGeometry.HalfOwner(landing.z) != p.Slot;
        }

        /// <summary>A first serve takes a risk; a second serve is a kick, which rarely misses.</summary>
        ShotButton ChooseServe(int serveNumber)
        {
            if (serveNumber >= 2) return ShotButton.Auto;
            double roll = rng.NextDouble();
            if (roll < 0.45) return ShotButton.Flat;
            if (roll < 0.75) return ShotButton.Slice;
            return ShotButton.Auto;
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
