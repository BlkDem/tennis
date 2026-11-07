using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    /// <summary>
    /// Translates player state into motion. This prototype version drives a primitive body procedurally.
    /// <see cref="Present"/> is the only entry point game code uses, so the Animator-based version of the
    /// next stage replaces this class's insides without touching any gameplay.
    /// </summary>
    public sealed class PlayerAnimationController : MonoBehaviour
    {
        // Arm yaw around the shoulder, in degrees: 0 points right, 90 behind, -90 straight ahead.
        const float ForehandBack = 75f;
        const float ForehandContact = -20f;
        const float ForehandFinish = -125f;
        const float BackhandBack = 145f;
        const float BackhandContact = 215f;
        const float BackhandFinish = 300f;
        const float NeutralYaw = -35f;

        const float IdlePitch = 25f;
        const float SwingPitch = 8f;
        const float OverheadPitch = -68f;
        const float SmashHeightForAnimation = 2.2f;

        public Transform body;
        public Transform armPivot;
        public float settleSpeed = 14f;
        public float maxLeanDegrees = 12f;
        public float stridesPerMetre = 0.9f;
        public float bobHeight = 0.05f;

        float yaw = NeutralYaw;
        float pitch = IdlePitch;
        float stridePhase;
        Vector3 bodyRestPosition;

        void Awake()
        {
            if (body != null) bodyRestPosition = body.localPosition;
        }

        /// <summary>Applies this frame's pose for the given player state.</summary>
        public void Present(PlayerAgent agent, Vector3 ballPosition)
        {
            AnimateBody(agent.Movement.Velocity, agent.Movement.Tuning.SprintSpeed);
            AnimateArm(agent.Shot, ballPosition);
        }

        void AnimateBody(Vector3 velocity, float topSpeed)
        {
            if (body == null) return;

            float speed = velocity.magnitude;
            stridePhase += speed * stridesPerMetre * Time.deltaTime * Mathf.PI * 2f;
            float ratio = Mathf.Clamp01(speed / Mathf.Max(topSpeed, 0.1f));

            body.localPosition = bodyRestPosition + Vector3.up * (Mathf.Abs(Mathf.Sin(stridePhase)) * bobHeight * ratio);

            // Lean into the run, expressed in the body's own frame.
            Vector3 localVelocity = transform.InverseTransformDirection(velocity);
            Quaternion lean = Quaternion.Euler(localVelocity.z / Mathf.Max(topSpeed, 0.1f) * maxLeanDegrees, 0f, -localVelocity.x / Mathf.Max(topSpeed, 0.1f) * maxLeanDegrees);
            body.localRotation = Quaternion.Slerp(body.localRotation, lean, 1f - Mathf.Exp(-settleSpeed * Time.deltaTime));
        }

        void AnimateArm(PlayerShotController shot, Vector3 ballPosition)
        {
            if (armPivot == null) return;

            bool forehand = shot.DisplaySide == HandSide.Forehand;
            float back = forehand ? ForehandBack : BackhandBack;
            float contact = forehand ? ForehandContact : BackhandContact;
            float finish = forehand ? ForehandFinish : BackhandFinish;
            bool overhead = ballPosition.y >= SmashHeightForAnimation;

            float blend = 1f - Mathf.Exp(-settleSpeed * Time.deltaTime);
            switch (shot.Phase)
            {
                case SwingPhase.Preparing:
                    yaw = Mathf.Lerp(yaw, back, blend);
                    pitch = Mathf.Lerp(pitch, overhead ? OverheadPitch : SwingPitch, blend);
                    break;

                case SwingPhase.Swinging:
                    float progress = shot.SwingProgress;
                    yaw = progress <= 1f
                        ? Mathf.Lerp(back, contact, progress * progress)
                        : Mathf.Lerp(contact, finish, Mathf.Clamp01((progress - 1f) * 1.2f));
                    pitch = Mathf.Lerp(pitch, overhead ? OverheadPitch : SwingPitch, blend);
                    break;

                case SwingPhase.Recovering:
                    yaw = Mathf.Lerp(yaw, finish, blend);
                    pitch = Mathf.Lerp(pitch, IdlePitch, blend);
                    break;

                default:
                    yaw = Mathf.Lerp(yaw, NeutralYaw, blend * 0.5f);
                    pitch = Mathf.Lerp(pitch, IdlePitch, blend * 0.5f);
                    break;
            }

            armPivot.localRotation = Quaternion.Euler(0f, yaw, -pitch);
        }
    }
}
