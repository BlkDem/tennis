using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    /// <summary>
    /// Shows a player's aim cursor on the court while they hold a shot on the keyboard: the spot the ball is aimed at.
    /// The shot still strays from it by how well the ball is met.
    /// </summary>
    public sealed class AimCursorView : MonoBehaviour
    {
        /// <summary>Just above the ball's landing marker, so the two never flicker through each other.</summary>
        const float GroundOffset = 0.016f;

        public PlayerController player;
        public Transform marker;
        [Tooltip("The marker turns slowly, so it reads as a live cursor rather than a court marking, degrees per second.")]
        public float spinSpeed = 90f;

        void LateUpdate()
        {
            if (marker == null) return;
            PlayerAgent agent = player != null ? player.Agent : null;
            RallySession session = player != null ? player.Session : null;
            bool inPlay = session != null && (session.State == SessionState.Rally || session.State == SessionState.Toss);
            bool show = agent != null && inPlay && agent.AimingWithCursor;
            if (marker.gameObject.activeSelf != show) marker.gameObject.SetActive(show);
            if (!show) return;

            Vector2 target = agent.AimTarget;
            marker.position = new Vector3(target.x, GroundOffset, target.y);
            marker.rotation = Quaternion.Euler(90f, Time.time * spinSpeed, 0f);
        }
    }
}
