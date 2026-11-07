using UnityEngine;

namespace TennisSim.Simulation
{
    /// <summary>Near plays on the -Z half and hits towards +Z; Far is the mirror image.</summary>
    public enum PlayerSlot { Near = 0, Far = 1 }

    /// <summary>
    /// Official court dimensions and zones. World layout: court centre at the origin,
    /// X across the court, Y up, Z along the court with the net on Z = 0.
    /// </summary>
    public static class CourtGeometry
    {
        public const float Length = 23.77f;
        public const float SinglesWidth = 8.23f;
        public const float DoublesWidth = 10.97f;

        public const float HalfLength = Length * 0.5f;
        public const float HalfSinglesWidth = SinglesWidth * 0.5f;
        public const float HalfDoublesWidth = DoublesWidth * 0.5f;
        public const float ServiceLineFromNet = 6.40f;

        public const float NetCenterHeight = 0.914f;
        public const float NetPostHeight = 1.07f;
        /// <summary>Singles sticks stand 0.914 m outside the singles sideline and hold the net at full height.</summary>
        public const float SinglesStickX = HalfSinglesWidth + 0.914f;
        public const float NetHalfWidth = HalfDoublesWidth + 0.914f;

        /// <summary>Height of the top of the net at a given X (the cable sags towards the centre strap).</summary>
        public static float NetHeightAt(float x)
        {
            float t = Mathf.Clamp01(Mathf.Abs(x) / SinglesStickX);
            return NetCenterHeight + (NetPostHeight - NetCenterHeight) * t * t;
        }

        /// <summary>+1 if the slot hits towards +Z, -1 otherwise.</summary>
        public static float ForwardSign(PlayerSlot slot) => slot == PlayerSlot.Near ? 1f : -1f;

        public static PlayerSlot Opponent(PlayerSlot slot) => slot == PlayerSlot.Near ? PlayerSlot.Far : PlayerSlot.Near;

        /// <summary>The slot that owns the half containing this Z.</summary>
        public static PlayerSlot HalfOwner(float z) => z < 0f ? PlayerSlot.Near : PlayerSlot.Far;

        /// <summary>Lines are part of the court, so any contact up to the outer edge of a line counts as in.</summary>
        public static bool IsInsideSingles(float x, float z) =>
            Mathf.Abs(x) <= HalfSinglesWidth && Mathf.Abs(z) <= HalfLength;

        /// <summary>World X sign of the half of the baseline the server stands behind: the server's right on the deuce side.</summary>
        public static float ServeSideSign(PlayerSlot server, bool deuceSide) => (deuceSide ? 1f : -1f) * ForwardSign(server);

        /// <summary>The box a serve must land in: on the receiver's half, diagonally across from the server.</summary>
        public static ServiceBox ServiceBoxFor(PlayerSlot server, bool deuceSide)
        {
            float boxX = -ServeSideSign(server, deuceSide);
            float boxZ = ForwardSign(server);
            return new ServiceBox
            {
                MinX = boxX > 0f ? 0f : -HalfSinglesWidth,
                MaxX = boxX > 0f ? HalfSinglesWidth : 0f,
                MinZ = boxZ > 0f ? 0f : -ServiceLineFromNet,
                MaxZ = boxZ > 0f ? ServiceLineFromNet : 0f,
            };
        }
    }

    /// <summary>A service box in world coordinates. Its lines count as in.</summary>
    public struct ServiceBox
    {
        public float MinX, MaxX, MinZ, MaxZ;

        public bool Contains(float x, float z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
        public float CenterX => (MinX + MaxX) * 0.5f;
    }
}
