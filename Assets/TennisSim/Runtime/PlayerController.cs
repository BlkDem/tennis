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
        [Tooltip("How far from facing the net the body may turn towards the ball, degrees.")]
        public float maxLookAngle = 35f;

        RallySession session;
        PlayerAnimationController animationController;
        Vector3 previousPosition;
        Vector3 currentPosition;

        public PlayerAgent Agent { get; private set; }
        public RallySession Session => session;
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
                animationController.Present(Agent, session);
        }

        void FaceTheBall()
        {
            Vector3 forward = StrikeZone.Forward(Agent.Slot);
            Vector3 facing = forward;

            // Walking to the next point: look where you are going.
            Vector3 velocity = Agent.Movement.Velocity;
            if (Agent.WalkTarget.HasValue && velocity.sqrMagnitude > 0.25f)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(velocity), turnSpeed * 0.6f * Time.deltaTime);
                return;
            }

            // A shot or a serve faces the net: the clips are authored for a strike zone measured from there.
            SwingPhase phase = Agent.Shot.Phase;
            bool serving = Agent.Serve.Phase == ServePhase.Tossing || Agent.Serve.Phase == ServePhase.Swinging
                || (session.State == SessionState.AwaitingServe && session.Server == Agent.Slot);
            if (phase != SwingPhase.Preparing && phase != SwingPhase.Swinging && !serving)
            {
                Vector3 toBall = session.Ball.Position - Position;
                toBall.y = 0f;
                if (toBall.sqrMagnitude > 0.25f)
                    facing = Vector3.RotateTowards(forward, toBall.normalized, maxLookAngle * Mathf.Deg2Rad, 0f);
            }

            Quaternion target = Quaternion.LookRotation(facing);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
        }
    }
}
