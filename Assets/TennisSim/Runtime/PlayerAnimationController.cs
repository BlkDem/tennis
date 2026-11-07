using System.Collections.Generic;
using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    /// <summary>
    /// Translates player state into Animator states. Game code only calls <see cref="Present"/>, so a new
    /// model or clip set needs a controller with the same state names and no gameplay changes.
    /// Legs run on the base layer and the shot plays on the upper body, so a player can swing on the run.
    /// The serve uses the whole body and plays on the base layer.
    /// </summary>
    public sealed class PlayerAnimationController : MonoBehaviour
    {
        const int BaseLayer = 0;
        const int UpperLayer = 1;
        const string Prepare = "_Prep";
        const string Swing = "_Swing";
        const string Locomotion = "Locomotion";
        const string Celebrate = "Celebrate";
        const string PointLost = "PointLost";
        const string ServeReady = "Serve_Ready";
        const string ServeToss = "Serve_Toss";
        const string ServeSwing = "Serve_Swing";
        const float SmashHeight = 2.2f;
        /// <summary>Seconds into every swing clip at which the racket is at its contact pose.</summary>
        const float ClipContactTime = 0.14f;
        const float MinSwingSpeed = 0.6f;
        const float MaxSwingSpeed = 3f;
        const float StandingSpeed = 0.3f;

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
        public float serveFade = 0.12f;
        [Tooltip("How fast the upper body blends between the shot and the run, per second.")]
        public float upperBlendSpeed = 9f;
        public float moveDamping = 0.08f;

        readonly Dictionary<string, float> clipLengths = new Dictionary<string, float>();

        PlayerSlot slot;
        bool bound;
        SwingPhase lastPhase;
        ServePhase lastServePhase;
        string currentPrepare;
        string baseState = Locomotion;
        float upperBusyUntil;
        float baseBusyUntil;
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
            animator.CrossFadeInFixedTime(reaction, reactionFade, BaseLayer);
            baseState = reaction;
            reactionUntil = Time.time + clipLengths[reaction];
            upperBusyUntil = 0f;
        }

        /// <summary>Applies this frame's animation for the given player state.</summary>
        public void Present(PlayerAgent agent, RallySession session)
        {
            slot = agent.Slot;
            bound = true;
            lastSimulationTime = session.Time;

            Vector3 localVelocity = transform.InverseTransformDirection(agent.Movement.Velocity);
            animator.SetFloat(MoveX, localVelocity.x, moveDamping, Time.deltaTime);
            animator.SetFloat(MoveZ, localVelocity.z, moveDamping, Time.deltaTime);
            bool inPlay = session.State == SessionState.Rally || session.State == SessionState.Toss;
            animator.SetBool(Engaged, inPlay || localVelocity.sqrMagnitude > 0.25f);

            UpdateServe(agent, session, localVelocity.magnitude);
            UpdateUpperBody(agent.Shot, session.Ball.Position);
        }

        void UpdateServe(PlayerAgent agent, RallySession session, float speed)
        {
            ServePhase phase = agent.Serve.Phase;
            bool waitingToServe = session.State == SessionState.AwaitingServe && session.Server == slot && phase == ServePhase.Idle;

            if (phase == ServePhase.Tossing && lastServePhase != ServePhase.Tossing)
            {
                SetBase(ServeToss, serveFade);
            }
            else if (phase == ServePhase.Swinging && lastServePhase != ServePhase.Swinging)
            {
                animator.SetFloat(SwingSpeed, 1f);
                SetBase(ServeSwing, swingFade);
                baseBusyUntil = Time.time + clipLengths[ServeSwing] * 0.9f;
            }
            else if (waitingToServe && speed < StandingSpeed && Time.time >= reactionUntil)
            {
                if (baseState != ServeReady) SetBase(ServeReady, serveFade * 2f);
            }
            else if (IsServeState(baseState) && phase != ServePhase.Tossing && phase != ServePhase.Swinging && Time.time >= baseBusyUntil)
            {
                SetBase(Locomotion, serveFade * 2f);
            }
            lastServePhase = phase;
        }

        void SetBase(string state, float fade)
        {
            animator.CrossFadeInFixedTime(state, fade, BaseLayer);
            baseState = state;
        }

        static bool IsServeState(string state) => state == ServeReady || state == ServeToss || state == ServeSwing;

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

        /// <summary>Slices and drop shots swing high to low; a ball the player cannot quite reach is sliced back too.</summary>
        static string ShotFamily(PlayerShotController shot, Vector3 ballPosition)
        {
            ContactPlan plan = shot.Plan;
            if (plan.Valid && plan.BallPoint.y >= SmashHeight && !plan.BallHasBounced) return "Smash";
            if (!plan.Valid && ballPosition.y >= SmashHeight && shot.Phase == SwingPhase.Swinging) return "Smash";
            if (plan.Valid && !plan.BallHasBounced && shot.DisplaySide == HandSide.Forehand) return "Volley";

            bool slice = shot.ActiveButton == ShotButton.Slice || shot.ActiveButton == ShotButton.Drop || (plan.Valid && !plan.Reachable);
            string side = shot.DisplaySide == HandSide.Forehand ? "Forehand" : "Backhand";
            return slice ? side + "Slice" : side;
        }
    }
}
