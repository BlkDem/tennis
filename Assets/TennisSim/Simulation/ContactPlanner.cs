using UnityEngine;

namespace TennisSim.Simulation
{
    public struct ContactPlan
    {
        public bool Valid;
        /// <summary>False when the player cannot make it in time: the plan is then a best effort.</summary>
        public bool Reachable;
        public float Time;
        public Vector3 BallPoint;
        public Vector3 Stance;
        public HandSide Side;
        public bool BallHasBounced;
    }

    /// <summary>Where the racket should meet the ball relative to the player's feet.</summary>
    public static class StrikeZone
    {
        public static Vector3 Right(PlayerSlot slot) => new Vector3(CourtGeometry.ForwardSign(slot), 0f, 0f);
        public static Vector3 Forward(PlayerSlot slot) => new Vector3(0f, 0f, CourtGeometry.ForwardSign(slot));

        /// <summary>Horizontal strike offset from the feet. Overhead shots are played nearly in line with the body.</summary>
        public static Vector3 Offset(PlayerSlot slot, HandSide side, float ballHeight, ShotTuning t)
        {
            bool overhead = ballHeight >= t.SmashMinHeight;
            float lateral = overhead ? t.OverheadLateral : t.StrikeLateral;
            float forward = overhead ? t.OverheadForward : t.StrikeForward;
            float sideSign = side == HandSide.Forehand ? 1f : -1f;
            return Right(slot) * (lateral * sideSign) + Forward(slot) * forward;
        }
    }

    /// <summary>Chooses the moment to take the ball by scanning its predicted flight.</summary>
    public static class ContactPlanner
    {
        const float SameSideBias = 0.15f;
        const float MinLeadTime = 0.02f;
        const float NetMargin = 0.1f;
        const float TravelCostPerMetre = 0.25f;
        const float AirborneContactCost = 1f;

        public static ContactPlan Find(BallTrajectory trajectory, Vector3 feet, PlayerSlot slot, HandSide? forcedSide,
            ShotButton button, float now, float maxSpeed, PlayerMovementTuning movement, ShotTuning t)
        {
            ContactPlan best = default;
            float bestCost = float.PositiveInfinity;
            float forward = CourtGeometry.ForwardSign(slot);

            foreach (TrajectorySample sample in trajectory.Samples)
            {
                if (sample.Bounces >= 2) break;
                float lead = sample.Time - now;
                if (lead < MinLeadTime) continue;

                Vector3 ball = sample.State.Position;
                if (ball.z * forward > -NetMargin) continue;
                if (ball.y < t.MinContactHeight || ball.y > t.MaxContactHeight) continue;

                bool bounced = sample.Bounces > 0;
                float baseCost = ContactCost(ball.y, bounced, button, lead, t);

                if (forcedSide != HandSide.Backhand)
                    Consider(HandSide.Forehand, 0f, ball, bounced, sample.Time, lead, baseCost, feet, slot, maxSpeed, movement, t, ref best, ref bestCost);
                if (forcedSide != HandSide.Forehand)
                    Consider(HandSide.Backhand, SameSideBias, ball, bounced, sample.Time, lead, baseCost, feet, slot, maxSpeed, movement, t, ref best, ref bestCost);
            }
            return best;
        }

        static void Consider(HandSide side, float sideCost, Vector3 ball, bool bounced, float time, float lead, float baseCost,
            Vector3 feet, PlayerSlot slot, float maxSpeed, PlayerMovementTuning movement, ShotTuning t, ref ContactPlan best, ref float bestCost)
        {
            Vector3 stance = movement.ClampToHalf(ball - StrikeZone.Offset(slot, side, ball.y, t), slot);
            stance.y = 0f;
            float travel = Vector3.Distance(new Vector3(feet.x, 0f, feet.z), stance);
            float need = travel / Mathf.Max(maxSpeed, 0.1f);
            bool reachable = need <= lead;

            // Prefer a contact near where the player chose to stand. An unreachable one never beats a reachable one.
            float cost = baseCost + sideCost + travel * TravelCostPerMetre;
            if (!reachable) cost += 10f + (need - lead) * 5f;

            if (cost < bestCost)
            {
                bestCost = cost;
                best = new ContactPlan
                {
                    Valid = true,
                    Reachable = reachable,
                    Time = time,
                    BallPoint = ball,
                    Stance = stance,
                    Side = side,
                    BallHasBounced = bounced,
                };
            }
        }

        static float ContactCost(float height, bool bounced, ShotButton button, float lead, ShotTuning t)
        {
            float heightCost = Mathf.Abs(height - t.IdealContactHeight);
            float cost;

            if (!bounced && height >= t.SmashMinHeight && button != ShotButton.Lob && button != ShotButton.Drop)
                cost = -0.5f; // A high ball: go up and smash it rather than wait for the bounce.
            else if (button == ShotButton.Volley)
                cost = bounced ? 4f + heightCost : 0.25f * lead; // Meet it as early as possible.
            else
                cost = bounced ? heightCost : AirborneContactCost + heightCost; // Normally let it bounce, unless the player is already inside the ball's flight.

            return cost + lead * 0.05f;
        }
    }
}
