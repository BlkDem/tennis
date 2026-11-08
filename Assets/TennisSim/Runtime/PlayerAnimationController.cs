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
        const string SplitStep = "SplitStep";
        const float SplitStepMaxSpeed = 1.5f;
        const float SmashHeight = 2.2f;
        /// <summary>Seconds into every swing clip at which the racket is at its contact pose.</summary>
        const float ClipContactTime = 0.14f;
        const float MinSwingSpeed = 0.25f;
        /// <summary>How much of the follow-through plays before preparing the next shot may cut it, seconds after contact.</summary>
        const float FollowThroughBeforeNextShot = 0.25f;
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
        public float upperBlendSpeed = 14f;
        public float moveDamping = 0.08f;

        readonly Dictionary<string, float> clipLengths = new Dictionary<string, float>();

        PlayerSlot slot;
        bool bound;
        ServePhase lastServePhase;
        string currentPrepare;
        string baseState = Locomotion;
        float baseBusyUntil;
        float reactionUntil;
        bool splitStepPending;
        float splitStepUntil;
        RallySession session;
        ShotTuning shotTuning;
        string visibleSwing;
        float hitAt = float.NaN;

        /// <summary>The legs play locomotion or stand ready, so foot planting may take them over. Serves, hops and reactions keep their own feet.</summary>
        public bool FeetFree => baseState == Locomotion || baseState == ServeReady;

        void Awake()
        {
            foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
                clipLengths[clip.name] = clip.length;
        }

        void OnEnable() => GameEvents.RallyEvent += OnRallyEvent;
        void OnDisable() => GameEvents.RallyEvent -= OnRallyEvent;

        void OnRallyEvent(RallyEvent e)
        {
            if (!bound) return;
            if (e.Type == RallyEventType.Hit && e.Slot != slot) splitStepPending = true;
            if (e.Type == RallyEventType.Hit && e.Slot == slot && visibleSwing != null && session != null) hitAt = session.Time;
            if (e.Type != RallyEventType.PointEnded) return;

            string reaction = e.Slot == slot ? Celebrate : PointLost;
            animator.CrossFadeInFixedTime(reaction, reactionFade, BaseLayer);
            baseState = reaction;
            reactionUntil = Time.time + clipLengths[reaction];
        }

        /// <summary>Applies this frame's animation for the given player state.</summary>
        public void Present(PlayerAgent agent, RallySession session)
        {
            slot = agent.Slot;
            bound = true;
            this.session = session;
            shotTuning = agent.ShotTuning;
            // The simulation's clock at this rendered moment, between two ticks.
            float now = session.Time + Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime) * Time.fixedDeltaTime;

            Vector3 localVelocity = transform.InverseTransformDirection(agent.Movement.Velocity);
            animator.SetFloat(MoveX, localVelocity.x, moveDamping, Time.deltaTime);
            animator.SetFloat(MoveZ, localVelocity.z, moveDamping, Time.deltaTime);
            bool inPlay = session.State == SessionState.Rally || session.State == SessionState.Toss;
            animator.SetBool(Engaged, inPlay || localVelocity.sqrMagnitude > 0.25f);

            // A celebration ends as soon as the player sets off to the next point; left alone, the controller's own
            // transition takes it back to locomotion when the clip ends.
            bool reacting = baseState == Celebrate || baseState == PointLost;
            if (reacting && localVelocity.magnitude > 0.6f)
            {
                SetBase(Locomotion, reactionFade);
                reactionUntil = 0f;
            }
            else if (reacting && Time.time >= reactionUntil)
            {
                baseState = Locomotion;
            }

            UpdateServe(agent, session, localVelocity.magnitude);
            UpdateSplitStep(agent, session, localVelocity.magnitude);
            UpdateUpperBody(agent.Shot, session.Ball.Position, now);
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

        /// <summary>
        /// As the opponent strikes, a player who is set gives a small hop and lands wide, ready to push off either way.
        /// Anyone already running skips it, and starting to run cuts it short.
        /// </summary>
        void UpdateSplitStep(PlayerAgent agent, RallySession session, float speed)
        {
            if (splitStepPending)
            {
                splitStepPending = false;
                bool set = speed < SplitStepMaxSpeed && agent.Shot.Phase == SwingPhase.Idle && baseState == Locomotion;
                if (set && session.State == SessionState.Rally)
                {
                    SetBase(SplitStep, 0.06f);
                    splitStepUntil = Time.time + clipLengths[SplitStep] * 0.85f;
                }
            }
            if (baseState == SplitStep && (Time.time >= splitStepUntil || speed > SplitStepMaxSpeed * 1.6f))
                SetBase(Locomotion, 0.12f);
        }

        void SetBase(string state, float fade)
        {
            animator.CrossFadeInFixedTime(state, fade, BaseLayer);
            baseState = state;
        }

        static bool IsServeState(string state) => state == ServeReady || state == ServeToss || state == ServeSwing;

        /// <summary>
        /// The swing is locked to the contact: the clip's contact pose must be on screen at the moment the racket meets
        /// the ball. So the visible swing starts a clip's contact time ahead of the expected contact, even while the
        /// button is still held (holding swings by itself just before the ball arrives), and every frame its speed is
        /// nudged so the clip runs on time. Once the ball is struck the clip plays out at its own pace.
        /// </summary>
        void UpdateUpperBody(PlayerShotController shot, Vector3 ballPosition, float now)
        {
            SwingPhase phase = shot.Phase;
            float latency = shotTuning != null ? shotTuning.SwingLatency : ClipContactTime;
            // Parameters set now are used when the Animator next evaluates, a frame later.
            float shown = now + Time.deltaTime;

            float expected = ExpectedContact(shot, latency);
            bool contactComing = !float.IsNaN(expected) && shown >= expected - ClipContactTime;
            bool swingDue = phase == SwingPhase.Swinging || (phase == SwingPhase.Preparing && contactComing);

            if (swingDue && visibleSwing == null)
            {
                visibleSwing = ShotFamily(shot, ballPosition) + Swing;
                hitAt = float.NaN;
                // Started late, the clip begins part way through rather than lagging the ball.
                float offset = Mathf.Max(0f, ClipContactTime - (expected - shown));
                animator.CrossFadeInFixedTime(visibleSwing, swingFade, UpperLayer, offset);
                animator.SetLayerWeight(UpperLayer, 1f);
                reactionUntil = 0f;
            }
            else if (phase == SwingPhase.Preparing)
            {
                // A new shot being prepared cuts the last swing's follow-through short.
                if (visibleSwing != null && !float.IsNaN(hitAt) && ClipTime(visibleSwing) > ClipContactTime + FollowThroughBeforeNextShot)
                    visibleSwing = null;
                string state = ShotFamily(shot, ballPosition) + Prepare;
                if (visibleSwing == null && (state != currentPrepare || float.IsNaN(ClipTime(state))))
                {
                    animator.CrossFadeInFixedTime(state, prepareFade, UpperLayer);
                    currentPrepare = state;
                }
            }

            if (visibleSwing != null)
            {
                // Where the clip really is, and the speed that puts it where it should be when it is next shown.
                float clip = ClipTime(visibleSwing);
                float target = float.IsNaN(hitAt) && !float.IsNaN(expected) ? ClipContactTime + (shown - expected) : float.NaN;
                float speed = 1f;
                if (!float.IsNaN(target) && !float.IsNaN(clip) && Time.deltaTime > 0f)
                    speed = Mathf.Clamp((target - clip) / Time.deltaTime, MinSwingSpeed, MaxSwingSpeed);
                animator.SetFloat(SwingSpeed, speed);

                bool finished = !float.IsNaN(clip) && clip >= clipLengths[visibleSwing] - 0.1f;
                bool lost = float.IsNaN(clip) && !animator.IsInTransition(UpperLayer);
                if (finished || lost || (phase == SwingPhase.Preparing && !contactComing && float.IsNaN(hitAt)))
                    visibleSwing = null;
            }

            bool shotActive = phase == SwingPhase.Preparing || phase == SwingPhase.Swinging || visibleSwing != null;
            float weightTarget = shotActive && Time.time >= reactionUntil ? 1f : 0f;
            float weight = Mathf.MoveTowards(animator.GetLayerWeight(UpperLayer), weightTarget, upperBlendSpeed * Time.deltaTime);
            animator.SetLayerWeight(UpperLayer, weight);
        }

        /// <summary>Seconds into the given upper-body state, whether it is playing or being faded to; NaN if neither.</summary>
        float ClipTime(string state)
        {
            if (animator.IsInTransition(UpperLayer))
            {
                AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(UpperLayer);
                if (next.IsName(state)) return next.normalizedTime * clipLengths[state];
            }
            AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(UpperLayer);
            return current.IsName(state) ? current.normalizedTime * clipLengths[state] : float.NaN;
        }

        /// <summary>
        /// When the racket will meet the ball, in simulation time: the moment it did, once it has; while swinging, the
        /// earlier of the sweet spot and the ball's arrival (a ball passing through reach is struck there); while
        /// preparing, the planned contact. NaN when no contact is expected.
        /// </summary>
        float ExpectedContact(PlayerShotController shot, float latency)
        {
            if (!float.IsNaN(hitAt)) return hitAt;
            float plan = shot.Plan.Valid ? shot.Plan.Time : float.PositiveInfinity;
            if (shot.Phase == SwingPhase.Swinging) return Mathf.Min(shot.SwingStart + latency, plan);
            return shot.Phase == SwingPhase.Preparing && shot.Plan.Valid ? plan : float.NaN;
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
