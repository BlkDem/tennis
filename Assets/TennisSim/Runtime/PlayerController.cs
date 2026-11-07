using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    /// <summary>Presents one player on court. Movement, shots and decisions all live in <see cref="PlayerAgent"/>.</summary>
    public sealed class PlayerController : MonoBehaviour
    {
        const float TeleportDistance = 3f;

        [Tooltip("How fast the body turns to keep facing the ball, degrees per second.")]
        public float turnSpeed = 540f;

        RallySession session;
        PlayerAnimationController animationController;
        Vector3 previousPosition;
        Vector3 currentPosition;

        public PlayerAgent Agent { get; private set; }
        public Vector3 Position { get; private set; }

        public void Bind(RallySession rally, PlayerSlot slot)
        {
            session = rally;
            Agent = rally.Agent(slot);
            animationController = GetComponent<PlayerAnimationController>();
            currentPosition = previousPosition = Position = Agent.Position;
            transform.position = Position;
            transform.rotation = Quaternion.LookRotation(StrikeZone.Forward(slot));
        }

        public void OnSessionTicked()
        {
            previousPosition = currentPosition;
            currentPosition = Agent.Position;
            if (Vector3.Distance(previousPosition, currentPosition) > TeleportDistance)
                previousPosition = currentPosition;
        }

        void LateUpdate()
        {
            if (Agent == null) return;

            float alpha = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
            Position = Vector3.Lerp(previousPosition, currentPosition, alpha);
            transform.position = Position;

            FaceTheBall();

            if (animationController != null)
                animationController.Present(Agent, session.Ball.Position);
        }

        void FaceTheBall()
        {
            Vector3 forward = StrikeZone.Forward(Agent.Slot);
            Vector3 toBall = session.Ball.Position - Position;
            toBall.y = 0f;

            // Keep the chest towards the opponent's half even when the ball is behind the shoulder.
            Vector3 facing = toBall.sqrMagnitude > 0.25f ? Vector3.RotateTowards(forward, toBall.normalized, 60f * Mathf.Deg2Rad, 0f) : forward;
            Quaternion target = Quaternion.LookRotation(facing);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
        }
    }
}
