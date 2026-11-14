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
        const string WarmUp = "WarmUp";
        const string Greet = "Greet";
        const string Defeat = "Defeat";
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
        /// <summary>Faster than this after the serve, m/s, the legs leave the serve's follow-through and run.</summary>
        const float ServeRunOffSpeed = 1.2f;

        static readonly int Speed = Animator.StringToHash("Speed");
        static readonly int DirX = Animator.StringToHash("DirX");
        static readonly int DirZ = Animator.StringToHash("DirZ");
        static readonly int Engaged = Animator.StringToHash("Engaged");
        static readonly int SwingSpeed = Animator.StringToHash("SwingSpeed");
        static readonly int StrideRate = Animator.StringToHash("StrideRate");
        const float MinStrideRate = 0.4f;
        const float MaxStrideRate = 2.5f;

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

        /// <summary>
        /// The gaits: the speed each clip's planted foot goes back at, and the speeds over which it plays alone, faster
        /// or slower to keep pace. The clips are motion capture (rig_and_animate.py); the speeds are the planted foot's,
        /// which in a run is a little slower than the body's average. Between the walk and the run there is only a short
        /// blend: their footfalls differ, so mixed they partly cancel each other's strides, and the IK rig holds the
        /// planted foot meanwhile. ProjectBuilder builds the blend from this table.
        /// </summary>
        public static readonly (string name, float authored, float from, float to)[] Gaits =
        {
            ("Walk", 1.19f, 0.6f, 1.9f),
            ("Run", 4.05f, 2.4f, 2.4f),
        };

        /// <summary>
        /// Where each gait's two retreat clips travel, degrees either side of straight ahead (RETREAT_TRAVEL in the
        /// Blender script). A retreat turns the hips one way or the other, and a blend of both turns would cancel them
        /// into a skate, so closer to straight back than this the blend is kept to one side.
        /// </summary>
        public const float RetreatSideAngle = 175f;

        /// <summary>
        /// The direction the locomotion blend is given for a direction of travel, both in the player's frame: the same,
        /// except near straight back, where it is kept to the side the player last went (+1 right, -1 left).
        /// </summary>
        public static Vector2 BlendDirection(Vector2 travel, ref float retreatSide)
        {
            float heading = Mathf.Atan2(travel.x, travel.y) * Mathf.Rad2Deg;
            if (Mathf.Abs(heading) <= RetreatSideAngle)
            {
                if (Mathf.Abs(heading) > 1f) retreatSide = Mathf.Sign(heading);
                return travel;
            }
            heading = retreatSide * RetreatSideAngle * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(heading), Mathf.Cos(heading));
        }

        /// <summary>
        /// The speed the locomotion blend shows at its normal rate for a given player speed: the authored speeds of the
        /// gaits it is mixing, weighted as the blend weights them. From standing to the first gait the standing pose
        /// has no stride, so the walk's share of the blend does all the travelling.
        /// </summary>
        public static float BlendedGaitSpeed(float speed)
        {
            float previousAt = 0f, previousSpeed = 0f;
            foreach ((string _, float authored, float from, float to) in Gaits)
            {
                if (speed <= from)
                {
                    float w = (speed - previousAt) / Mathf.Max(from - previousAt, 1e-3f);
                    return Mathf.Lerp(previousSpeed, authored, w);
                }
                if (speed <= to) return authored;
                previousAt = to;
                previousSpeed = authored;
            }
            return previousSpeed;
        }

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
        Vector3 shownVelocity;
        Vector3 shownVelocityRate;
        Vector2 direction = Vector2.up;
        float retreatSide = 1f;

        /// <summary>
        /// The legs play locomotion, so foot planting may take them over. Serves, hops and reactions keep their own feet,
        /// and so does a change from one to another: the clip moves the feet to its new stance, and feet held to the
        /// court meanwhile would drag the legs sideways.
        /// </summary>
        /// <summary>Set while the players warm up before the menu's exhibition match: the warm-up plays instead of play.</summary>
        public bool WarmingUp { get; set; }

        public bool FeetFree => baseState == Locomotion && !animator.IsInTransition(BaseLayer);

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

            // The last point of a match: the winner waves to the crowd, the loser hangs their head.
            string reaction = e.ScoreChange == ScoreChange.Match ? (e.Slot == slot ? Greet : Defeat) : (e.Slot == slot ? Celebrate : PointLost);
            animator.CrossFadeInFixedTime(reaction, reactionFade, BaseLayer);
            baseState = reaction;
            reactionUntil = Time.time + clipLengths[reaction];
        }

        /// <summary>
        /// Feeds the locomotion its speed and direction, and plays it at the rate that makes the planted foot go back as
        /// fast as the player goes forward, so nothing skates. The direction is kept while the player slows to a stop,
        /// where the standing clip takes over anyway.
        /// </summary>
        void UpdateLocomotion(Vector3 localVelocity)
        {
            shownVelocity = Vector3.SmoothDamp(shownVelocity, new Vector3(localVelocity.x, 0f, localVelocity.z), ref shownVelocityRate, moveDamping);
            var flat = new Vector2(shownVelocity.x, shownVelocity.z);
            float speed = flat.magnitude;
            if (speed > 0.05f) direction = flat / speed;
            Vector2 shownDirection = BlendDirection(direction, ref retreatSide);
            animator.SetFloat(Speed, speed);
            animator.SetFloat(DirX, shownDirection.x);
            animator.SetFloat(DirZ, shownDirection.y);
            // The blend follows the smoothed speed, which lags a player bursting into a run; the rate makes up the
            // difference, so the planted foot keeps pace with the real one.
            float shown = BlendedGaitSpeed(speed);
            float real = new Vector2(localVelocity.x, localVelocity.z).magnitude;
            animator.SetFloat(StrideRate, shown > 0.05f ? Mathf.Clamp(real / shown, MinStrideRate, MaxStrideRate) : 1f);
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

            if (WarmingUp)
            {
                // Each player runs through the warm-up from their own point in it, so the two do not move as one.
                if (baseState != WarmUp)
                    animator.CrossFadeInFixedTime(WarmUp, reactionFade, BaseLayer, slot == PlayerSlot.Far ? clipLengths[WarmUp] * 0.4f : 0f);
                baseState = WarmUp;
                return;
            }
            if (baseState == WarmUp) SetBase(Locomotion, reactionFade * 2f);

            Vector3 localVelocity = transform.InverseTransformDirection(agent.Movement.Velocity);
            UpdateLocomotion(localVelocity);
            bool inPlay = session.State == SessionState.Rally || session.State == SessionState.Toss;
            animator.SetBool(Engaged, inPlay || localVelocity.sqrMagnitude > 0.25f);

            // A celebration ends as soon as the player sets off to the next point; left alone, the controller's own
            // transition takes it back to locomotion when the clip ends.
            bool reacting = baseState == Celebrate || baseState == PointLost || baseState == Greet || baseState == Defeat;
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
            else if (IsServeState(baseState) && phase != ServePhase.Tossing && phase != ServePhase.Swinging
                     && (Time.time >= baseBusyUntil || speed > ServeRunOffSpeed))
            {
                // A server setting off after the serve runs on their own legs: the serve's landing would skate along.
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
            // The hop has the feet in the air or planted wide: running on with it would skate, so setting off ends it.
            if (baseState == SplitStep && (Time.time >= splitStepUntil || speed > SplitStepMaxSpeed))
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
