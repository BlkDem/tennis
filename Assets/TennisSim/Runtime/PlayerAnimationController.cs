using System.Collections.Generic;
using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    /// <summary>
    /// Translates player state into Animator states. Game code only calls <see cref="Present"/>, so a new
    /// model or clip set needs a controller with the same state names and no gameplay changes.
    /// Legs run on the base layer and the shot plays on the upper body, so a player can swing on the run.
    /// </summary>
    public sealed class PlayerAnimationController : MonoBehaviour
    {
        const int UpperLayer = 1;
        const string Prepare = "_Prep";
        const string Swing = "_Swing";
        const string Recovery = "Recovery";
        const string Celebrate = "Celebrate";
        const string PointLost = "PointLost";
        const float SmashHeight = 2.2f;
        /// <summary>Seconds into every swing clip at which the racket is at its contact pose.</summary>
        const float ClipContactTime = 0.14f;
        const float MinSwingSpeed = 0.6f;
        const float MaxSwingSpeed = 3f;

        static readonly int MoveX = Animator.StringToHash("MoveX");
        static readonly int MoveZ = Animator.StringToHash("MoveZ");
        static readonly int Engaged = Animator.StringToHash("Engaged");
        static readonly int SwingSpeed = Animator.StringToHash("SwingSpeed");

        public Animator animator;
        [Tooltip("Crossfade into a shot's preparation pose, seconds.")]
        public float prepareFade = 0.1f;
        [Tooltip("Crossfade into a swing, seconds. Short, because the swing must keep the game's timing.")]
        public float swingFade = 0.03f;
        public float reactionFade = 0.2f;
        [Tooltip("How fast the upper body blends between the shot and the run, per second.")]
        public float upperBlendSpeed = 9f;
        public float moveDamping = 0.08f;

        readonly Dictionary<string, float> clipLengths = new Dictionary<string, float>();

        PlayerSlot slot;
        bool bound;
        SwingPhase lastPhase;
        string currentPrepare;
        float upperBusyUntil;
        float reactionUntil;
        float lastSimulationTime;

        void Awake()
        {
            foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
                clipLengths[clip.name] = clip.length;
        }

        void OnEnable() => GameEvents.RallyEvent += OnRallyEvent;
        void OnDisable() => GameEvents.RallyEvent -= OnRallyEvent;

        void OnRallyEvent(RallyEvent e)
        {
            if (!bound || e.Type != RallyEventType.PointEnded) return;

            string reaction = e.Slot == slot ? Celebrate : PointLost;
            animator.CrossFadeInFixedTime(reaction, reactionFade, 0);
            reactionUntil = Time.time + clipLengths[reaction];
            upperBusyUntil = 0f;
        }

        /// <summary>Applies this frame's animation for the given player state.</summary>
        /// <param name="inPlay">False between points, when a player standing still relaxes.</param>
        /// <param name="simulationTime">The gameplay clock, which contact plans are timed against.</param>
        public void Present(PlayerAgent agent, Vector3 ballPosition, bool inPlay, float simulationTime)
        {
            slot = agent.Slot;
            bound = true;
            lastSimulationTime = simulationTime;

            Vector3 localVelocity = transform.InverseTransformDirection(agent.Movement.Velocity);
            animator.SetFloat(MoveX, localVelocity.x, moveDamping, Time.deltaTime);
            animator.SetFloat(MoveZ, localVelocity.z, moveDamping, Time.deltaTime);
            animator.SetBool(Engaged, inPlay || localVelocity.sqrMagnitude > 0.25f);

            UpdateUpperBody(agent.Shot, ballPosition);
        }

        void UpdateUpperBody(PlayerShotController shot, Vector3 ballPosition)
        {
            SwingPhase phase = shot.Phase;
            string family = ShotFamily(shot, ballPosition);

            if (phase == SwingPhase.Preparing)
            {
                string state = family + Prepare;
                if (lastPhase != SwingPhase.Preparing || state != currentPrepare)
                {
                    animator.CrossFadeInFixedTime(state, prepareFade, UpperLayer);
                    currentPrepare = state;
                }
            }
            else if (phase == SwingPhase.Swinging && lastPhase != SwingPhase.Swinging)
            {
                string state = family + Swing;
                animator.SetFloat(SwingSpeed, SwingSpeedFor(shot));
                animator.CrossFadeInFixedTime(state, swingFade, UpperLayer);
                upperBusyUntil = Time.time + clipLengths[state] - 0.1f;
                reactionUntil = 0f;
            }
            lastPhase = phase;

            bool shotActive = phase == SwingPhase.Preparing || phase == SwingPhase.Swinging || Time.time < upperBusyUntil;
            float target = shotActive && Time.time >= reactionUntil ? 1f : 0f;
            float weight = Mathf.MoveTowards(animator.GetLayerWeight(UpperLayer), target, upperBlendSpeed * Time.deltaTime);
            animator.SetLayerWeight(UpperLayer, weight);
        }

        /// <summary>
        /// A swing can start closer to or further from the contact than its clip expects. Playing the clip
        /// at the right speed brings the racket to its contact pose just as the ball arrives.
        /// </summary>
        float SwingSpeedFor(PlayerShotController shot)
        {
            if (!shot.Plan.Valid) return 1f;
            float untilContact = shot.Plan.Time - lastSimulationTime;
            return untilContact > 0.01f ? Mathf.Clamp(ClipContactTime / untilContact, MinSwingSpeed, MaxSwingSpeed) : MaxSwingSpeed;
        }

        static string ShotFamily(PlayerShotController shot, Vector3 ballPosition)
        {
            ContactPlan plan = shot.Plan;
            if (plan.Valid && plan.BallPoint.y >= SmashHeight && !plan.BallHasBounced) return "Smash";
            if (!plan.Valid && ballPosition.y >= SmashHeight && shot.Phase == SwingPhase.Swinging) return "Smash";
            if (plan.Valid && !plan.BallHasBounced && shot.DisplaySide == HandSide.Forehand) return "Volley";
            return shot.DisplaySide == HandSide.Forehand ? "Forehand" : "Backhand";
        }
    }
}
