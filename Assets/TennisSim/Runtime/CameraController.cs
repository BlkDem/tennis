using TennisSim.Simulation;
using Unity.Cinemachine;
using UnityEngine;

namespace TennisSim
{
    /// <summary>
    /// Feeds a Cinemachine camera two smoothed proxies instead of the raw ball, which reverses direction
    /// at 40 m/s: a follow point behind the near player and a look point between the action and the court.
    /// </summary>
    public sealed class CameraController : MonoBehaviour
    {
        public CinemachineCamera virtualCamera;
        public Transform followTarget;
        public Transform lookTarget;
        public PlayerController trackedPlayer;
        public BallController ball;

        [Header("Framing")]
        [Range(25f, 70f)] public float fieldOfView = 42f;
        [Tooltip("The camera never comes closer to the net than this.")]
        public float minDistanceBehindCourtCentre = 13.5f;
        [Tooltip("Extra room kept behind the near player when they run deep.")]
        public float roomBehindPlayer = 0.6f;
        [Range(0f, 1f)] public float followPlayerX = 0.4f;
        [Range(0f, 1f)] public float followBallX = 0.12f;
        [Range(0f, 1f)] public float lookAtBallX = 0.35f;
        [Range(0f, 1f)] public float lookAtBallZ = 0.15f;
        [Range(0f, 1f)] public float lookAtBallHeight = 0.2f;
        public float lookBaseHeight = 1f;
        public float lookBaseZ = -0.5f;
        [Tooltip("Limits how far the camera tilts up towards the far court, so the near player's feet stay in frame.")]
        public float lookMaxZ = 1.5f;

        [Header("Smoothing, seconds")]
        public float followSmoothing = 0.45f;
        public float lookSmoothing = 0.35f;

        Vector3 followVelocity;
        Vector3 lookVelocity;

        void Start()
        {
            followTarget.position = DesiredFollow();
            lookTarget.position = DesiredLook();
        }

        void LateUpdate()
        {
            followTarget.position = Vector3.SmoothDamp(followTarget.position, DesiredFollow(), ref followVelocity, followSmoothing);
            lookTarget.position = Vector3.SmoothDamp(lookTarget.position, DesiredLook(), ref lookVelocity, lookSmoothing);

            if (virtualCamera != null) virtualCamera.Lens.FieldOfView = fieldOfView;
        }

        Vector3 DesiredFollow()
        {
            float x = trackedPlayer.Position.x * followPlayerX + ball.Position.x * followBallX;
            float deepest = Mathf.Min(-minDistanceBehindCourtCentre, trackedPlayer.Position.z - roomBehindPlayer);
            return new Vector3(x, 0f, deepest);
        }

        Vector3 DesiredLook()
        {
            Vector3 ballPosition = ball.Position;
            float z = Mathf.Clamp(lookBaseZ + ballPosition.z * lookAtBallZ, -CourtGeometry.HalfLength * 0.5f, lookMaxZ);
            float y = lookBaseHeight + Mathf.Max(0f, ballPosition.y - lookBaseHeight) * lookAtBallHeight;
            return new Vector3(ballPosition.x * lookAtBallX, y, z);
        }
    }
}
