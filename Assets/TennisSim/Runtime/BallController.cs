using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    /// <summary>Presents the simulated ball: smooth position between ticks, visible spin, ground shadow and landing marker.</summary>
    public sealed class BallController : MonoBehaviour
    {
        const float TeleportDistance = 4f;
        const float GroundOffset = 0.012f;

        public Transform visual;
        [Tooltip("Blob under the ball: at this game's speed it is the main cue for how high the ball is.")]
        public Transform shadow;
        public Transform landingMarker;
        public float shadowScaleAtGround = 0.28f;
        public float shadowScaleAtHeight = 0.5f;
        public float shadowFadeHeight = 8f;

        RallySession session;
        Vector3 previousPosition;
        Vector3 currentPosition;

        public Vector3 Position { get; private set; }

        public void Bind(RallySession rally)
        {
            session = rally;
            currentPosition = previousPosition = Position = session.Ball.Position;
        }

        public void OnSessionTicked()
        {
            previousPosition = currentPosition;
            currentPosition = session.Ball.Position;
            if (Vector3.Distance(previousPosition, currentPosition) > TeleportDistance)
                previousPosition = currentPosition;
        }

        void LateUpdate()
        {
            if (session == null) return;

            float alpha = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
            Position = Vector3.Lerp(previousPosition, currentPosition, alpha);
            transform.position = Position;

            UpdateSpin();
            UpdateShadow();
            UpdateLandingMarker();
        }

        void UpdateSpin()
        {
            Vector3 spin = session.Ball.Spin;
            float rate = spin.magnitude;
            if (visual != null && rate > 0.01f)
                visual.rotation = Quaternion.AngleAxis(rate * Mathf.Rad2Deg * Time.deltaTime, spin / rate) * visual.rotation;
        }

        void UpdateShadow()
        {
            if (shadow == null) return;
            shadow.position = new Vector3(Position.x, GroundOffset, Position.z);
            float size = Mathf.Lerp(shadowScaleAtGround, shadowScaleAtHeight, Mathf.Clamp01(Position.y / shadowFadeHeight));
            shadow.localScale = Vector3.one * size;
        }

        void UpdateLandingMarker()
        {
            if (landingMarker == null) return;
            bool show = session.State == SessionState.Rally && session.BouncesSinceHit == 0 && session.RallyHits > 0 && session.Trajectory.HasFirstBounce;
            if (landingMarker.gameObject.activeSelf != show) landingMarker.gameObject.SetActive(show);
            if (show)
            {
                Vector3 point = session.Trajectory.FirstBouncePoint;
                landingMarker.position = new Vector3(point.x, GroundOffset, point.z);
            }
        }
    }
}
