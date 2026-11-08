using System.Linq;
using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    /// <summary>
    /// Adjusts the animated pose after the Animator, so authored clips fit the ball they are played against:
    /// the hitting arm brings the strings to the ball at contact, a low ball is met with bent knees while the
    /// feet stay where the clip planted them, and the head follows the ball.
    /// Everything works in world space on the bones' current positions, so it is independent of bone axes.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class PlayerIkRig : MonoBehaviour
    {
        [Header("Racket to ball")]
        [Tooltip("How long before contact the arm starts reaching for the ball, seconds.")]
        public float reachBefore = 0.17f;
        [Tooltip("How long after contact it lets go, seconds.")]
        public float reachAfter = 0.14f;
        [Tooltip("Either side of the contact the racket is held fully on the ball, seconds.")]
        public float fullWeightSpan = 0.035f;
        [Tooltip("The arm only corrects this far; a ball further away is honestly missed.")]
        public float maxReach = 1.1f;
        [Tooltip("What the arm alone covers comfortably; beyond it the player leans towards the ball, metres.")]
        public float armReach = 0.3f;
        [Tooltip("Lean of the spine per metre the arm cannot cover, degrees.")]
        public float leanPerMetre = 35f;
        public float maxLean = 18f;
        [Tooltip("A ball above the reach of the stretched arm lifts the player onto the toes or off the ground, as on a serve, metres.")]
        public float maxLift = 0.22f;
        [Tooltip("How far the wrist may turn the racket head towards the ball, degrees.")]
        public float maxWristTurn = 40f;

        [Header("Low balls")]
        [Tooltip("Contact below this height bends the knees.")]
        public float crouchBelow = 1.0f;
        [Tooltip("Share of the height below Crouch Below that the knees take up.")]
        public float crouchShare = 0.8f;
        public float maxCrouch = 0.45f;

        [Header("Feet")]
        [Tooltip("Below this speed the feet stick to the court instead of sliding with the body, m/s.")]
        public float plantMaxSpeed = 0.7f;
        [Tooltip("A planted foot steps once the body has moved this far from it, metres...")]
        public float stepTrigger = 0.2f;
        [Tooltip("...or turned this far, degrees.")]
        public float stepTurn = 35f;
        public float stepDuration = 0.16f;
        public float stepHeight = 0.07f;
        [Tooltip("How long the clip takes to get a planted foot back when the player sets off, seconds.")]
        public float releaseTime = 0.12f;
        [Tooltip("How far the front foot steps in towards a shot taken standing, metres.")]
        public float stepInDistance = 0.3f;
        public float stepInBefore = 0.35f;
        public float stepInAfter = 0.3f;
        [Tooltip("How far the hips move onto the front foot, metres.")]
        public float weightTransfer = 0.06f;

        [Header("Head")]
        [Tooltip("Share of the turn towards the ball the head makes; the eyes would do the rest.")]
        [Range(0f, 1f)] public float lookWeight = 0.7f;
        public float maxLookYaw = 60f;
        public float maxLookDown = 25f;
        public float maxLookUp = 35f;
        public float lookSpeed = 10f;

        public BallController ball;

        PlayerController player;
        PlayerAnimationController animationController;
        RallySession session;
        readonly FootState[] footStates = { new FootState(), new FootState() };
        float footGround;
        float transfer;
        float lastHitAt = float.NegativeInfinity;
        const float TeleportDistance = 1.2f;

        Transform hips, spine, neck, head;
        Transform upperArm, lowerArm, hand, racket;
        Transform leftUpperArm, leftLowerArm, leftHand;
        const float TwoHandedGap = 0.2f;
        Transform[] upperLegs = new Transform[2], lowerLegs = new Transform[2], feet = new Transform[2];
        Vector3 racketHeadLocal;
        Vector3 headFaceLocal;
        Quaternion lookOffset = Quaternion.identity;

        float contactTime = float.NegativeInfinity;
        Vector3 contactPoint;
        bool hitRecorded;

        public bool Ready { get; private set; }
        /// <summary>For each finished reach: the closest the strings came to the contact point, metres.</summary>
        public System.Collections.Generic.List<float> ContactMisses { get; } = new System.Collections.Generic.List<float>();
        /// <summary>Why each reach ended where it did, for tuning.</summary>
        public System.Collections.Generic.List<string> ContactNotes { get; } = new System.Collections.Generic.List<string>();
        float closestThisReach = float.PositiveInfinity;
        string closestNote;
        float neededThisFrame;
        /// <summary>Where the face points as animated, before the look adjustment; for checks.</summary>
        public Vector3 AnimatedFace { get; private set; }
        /// <summary>The head's final face direction after all adjustments; for checks.</summary>
        public Vector3 FinalFace => head.rotation * headFaceLocal;
        public float LookAngle { get { lookOffset.ToAngleAxis(out float angle, out _); return angle; } }
        /// <summary>The middle of the strings, in world space, after the adjustment.</summary>
        public Vector3 RacketHead => racket.TransformPoint(racketHeadLocal);

        void Awake()
        {
            player = GetComponent<PlayerController>();
            animationController = GetComponent<PlayerAnimationController>();
            Transform[] all = GetComponentsInChildren<Transform>(true);
            Transform Bone(string name) => all.FirstOrDefault(t => t.name == name && t.GetComponent<Renderer>() == null);

            hips = Bone("Hips");
            spine = Bone("Spine");
            neck = Bone("Neck");
            head = Bone("Head");
            upperArm = Bone("R_UpperArm");
            lowerArm = Bone("R_LowerArm");
            hand = Bone("R_Hand");
            leftUpperArm = Bone("L_UpperArm");
            leftLowerArm = Bone("L_LowerArm");
            leftHand = Bone("L_Hand");
            racket = all.FirstOrDefault(t => t.name == "Racket" && t.parent == hand);
            for (int i = 0; i < 2; i++)
            {
                string side = i == 0 ? "L_" : "R_";
                upperLegs[i] = Bone(side + "UpperLeg");
                lowerLegs[i] = Bone(side + "LowerLeg");
                feet[i] = Bone(side + "Foot");
            }

            // The middle of the strings in the racket bone's space, from the strings mesh as it was bound.
            SkinnedMeshRenderer strings = GetComponentsInChildren<SkinnedMeshRenderer>().FirstOrDefault(r => r.name == "Strings");
            if (strings != null && racket != null)
            {
                int index = System.Array.IndexOf(strings.bones, racket);
                if (index >= 0) racketHeadLocal = strings.sharedMesh.bindposes[index].MultiplyPoint3x4(strings.sharedMesh.bounds.center);
            }

            // Which way the face points in the head bone's space, from the bind pose: the model faces its root's forward
            // when bound. The skeleton's starting pose cannot be used, as it is whatever clip was active at export.
            SkinnedMeshRenderer body = GetComponentsInChildren<SkinnedMeshRenderer>().FirstOrDefault(r => r.name == "Player");
            if (body != null && head != null)
            {
                int index = System.Array.IndexOf(body.bones, head);
                if (index >= 0)
                {
                    Quaternion headBound = body.sharedMesh.bindposes[index].inverse.rotation;
                    headFaceLocal = Quaternion.Inverse(headBound) * body.transform.InverseTransformDirection(transform.forward);
                }
                // The ankle's height above the ground when standing, also from the bind pose.
                int foot = feet[0] != null ? System.Array.IndexOf(body.bones, feet[0]) : -1;
                if (foot >= 0)
                    footGround = body.transform.TransformPoint(body.sharedMesh.bindposes[foot].inverse.MultiplyPoint3x4(Vector3.zero)).y - transform.position.y;
            }

            Ready = hips != null && spine != null && head != null && leftUpperArm != null && leftLowerArm != null && leftHand != null && neck != null && upperArm != null && lowerArm != null && hand != null
                    && racket != null && racketHeadLocal != Vector3.zero && headFaceLocal != Vector3.zero && upperLegs.All(t => t != null) && lowerLegs.All(t => t != null) && feet.All(t => t != null);
            if (!Ready) Debug.LogWarning($"[Tennis] {name}: the rig lacks bones for IK, so clips play unadjusted.");
        }

        void OnEnable() => GameEvents.RallyEvent += OnRallyEvent;
        void OnDisable() => GameEvents.RallyEvent -= OnRallyEvent;

        void OnRallyEvent(RallyEvent e)
        {
            if (session == null || player.Agent == null || e.Type != RallyEventType.Hit || e.Slot != player.Agent.Slot) return;
            // The real contact replaces the plan, and the arm lets go from there.
            contactTime = session.Time;
            contactPoint = e.Point;
            hitRecorded = true;
            if (player.Agent.Serve.Phase == ServePhase.Idle) lastHitAt = session.Time;
        }

        void LateUpdate()
        {
            if (!Ready || ball == null || player.Session == null || player.Agent == null) return;
            if (session != player.Session)
            {
                session = player.Session;
                contactTime = float.NegativeInfinity;
                lastHitAt = float.NegativeInfinity;
                hitRecorded = false;
            }

            float now = session.Time + Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime) * Time.fixedDeltaTime;
            UpdateContact(now);

            float weight = ContactWeight(now);
            // A two-handed grip: remember where the left hand holds the racket, to put it back there.
            bool twoHanded = weight > 0.001f && Vector3.Distance(leftHand.position, hand.position) < TwoHandedGap;
            Vector3 leftOnRacket = racket.InverseTransformPoint(leftHand.position);
            Quaternion leftRotation = Quaternion.Inverse(racket.rotation) * leftHand.rotation;

            LowerBody(now, weight);

            if (weight > 0.001f)
            {
                ReachForBall(weight);
                if (twoHanded)
                {
                    Vector3 elbowHint = leftLowerArm.position + (leftLowerArm.position - (leftUpperArm.position + leftHand.position) * 0.5f).normalized * 0.3f;
                    TwoBone(leftUpperArm, leftLowerArm, leftHand, racket.TransformPoint(leftOnRacket), elbowHint);
                    leftHand.rotation = racket.rotation * leftRotation;
                }
                float miss = Vector3.Distance(RacketHead, contactPoint);
                if (miss < closestThisReach)
                {
                    closestThisReach = miss;
                    float armLength = Vector3.Distance(upperArm.position, lowerArm.position) + Vector3.Distance(lowerArm.position, hand.position);
                    Vector3 local = transform.InverseTransformPoint(contactPoint);
                    closestNote = $"miss {miss:F2} w {weight:F2} needed {neededThisFrame:F2} ball h {contactPoint.y:F2} side {local.x:F2} fwd {local.z:F2} "
                                + $"shoulder-hand {Vector3.Distance(upperArm.position, hand.position) / armLength:F2} of arm";
                }
            }
            else if (!float.IsPositiveInfinity(closestThisReach))
            {
                ContactMisses.Add(closestThisReach);
                ContactNotes.Add(closestNote);
                closestThisReach = float.PositiveInfinity;
            }
            LookAtBall();
        }

        /// <summary>Where and when the racket should meet the ball: the shot's plan, the serve's toss, or the hit that just happened.</summary>
        void UpdateContact(float now)
        {
            PlayerAgent agent = player.Agent;
            if (hitRecorded && now - contactTime < reachAfter) return;
            hitRecorded = false;

            ServeController serve = agent.Serve;
            if (serve.Phase == ServePhase.Swinging)
            {
                contactTime = serve.SwingStart + agent.ShotTuning.SwingLatency;
                contactPoint = PredictBall(contactTime);
                return;
            }

            PlayerShotController shot = agent.Shot;
            if ((shot.Phase == SwingPhase.Preparing || shot.Phase == SwingPhase.Swinging) && shot.Plan.Valid)
            {
                contactTime = shot.Plan.Time;
                contactPoint = shot.Plan.BallPoint;
                return;
            }
            if (now - contactTime > reachAfter) contactTime = float.NegativeInfinity;
        }

        Vector3 PredictBall(float time)
        {
            BallTrajectory trajectory = session.Trajectory;
            if (trajectory == null || trajectory.Samples.Count == 0) return ball.Position;
            foreach (TrajectorySample sample in trajectory.Samples)
                if (sample.Time >= time) return sample.State.Position;
            return ball.Position;
        }

        /// <summary>Full weight for a moment either side of the contact, so whichever frame shows it has the racket on the ball.</summary>
        float ContactWeight(float now)
        {
            if (float.IsNegativeInfinity(contactTime)) return 0f;
            float t = now - contactTime;
            float flat = Mathf.Max(0f, Mathf.Abs(t) - fullWeightSpan);
            float span = (t < 0f ? reachBefore : reachAfter) - fullWeightSpan;
            float x = 1f - Mathf.Clamp01(flat / span);
            return x * x * (3f - 2f * x);
        }

        /// <summary>
        /// Hips and legs. The hips drop for a low ball, rise for a high one and shift onto a front foot stepping into the
        /// shot; the legs are then solved to where each foot should be: planted, stepping, or where the clip puts it.
        /// </summary>
        void LowerBody(float now, float weight)
        {
            float drop = weight > 0.001f ? Mathf.Clamp((crouchBelow - contactPoint.y) * crouchShare, 0f, maxCrouch) * weight : 0f;
            float lift = weight > 0.001f ? LiftFor(weight) : 0f;
            int front = PlanFeet(now);
            transfer = Mathf.MoveTowards(transfer, front >= 0 ? 1f : 0f, Time.deltaTime / 0.25f);

            bool feetMoved = footStates[0].release < 1f || footStates[1].release < 1f || front >= 0;
            if (drop < 0.002f && lift < 0.002f && transfer < 0.002f && !feetMoved) return;

            hips.position += Vector3.down * drop + Vector3.up * lift + transform.forward * (weightTransfer * transfer);
            for (int i = 0; i < 2; i++)
            {
                // Rising for a high ball takes the feet with it: onto the toes, or off the ground on a serve.
                Vector3 target = footStates[i].target + Vector3.up * lift;
                Vector3 kneeHint = lowerLegs[i].position + transform.forward * 0.5f;
                TwoBone(upperLegs[i], lowerLegs[i], feet[i], target, kneeHint);
                feet[i].rotation = footStates[i].targetRotation;
            }
        }

        /// <summary>A ball higher than the stretched arm and racket reach lifts the whole player, onto the toes or into a jump.</summary>
        float LiftFor(float weight)
        {
            float arm = Vector3.Distance(upperArm.position, lowerArm.position) + Vector3.Distance(lowerArm.position, hand.position);
            float top = upperArm.position.y + arm * 0.97f + Vector3.Distance(hand.position, RacketHead) * 0.9f;
            return Mathf.Clamp(contactPoint.y - top, 0f, maxLift) * weight;
        }

        sealed class FootState
        {
            public bool planted;
            public bool stepping;
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 from;
            public Quaternion fromRotation;
            public float progress;
            /// <summary>0 while the foot is held to the ground, 1 when it plays the clip freely.</summary>
            public float release = 1f;
            public Vector3 target;
            public Quaternion targetRotation;
        }

        /// <summary>
        /// Decides where each foot goes this frame. Standing or nearly so, feet stick to the court; when the body has
        /// turned or moved too far from a foot, it steps, one foot at a time, with a little lift. Before a shot taken
        /// standing, the front foot steps in towards the ball. Running, the clip has the feet. Returns the foot stepping
        /// into a shot, or -1.
        /// </summary>
        int PlanFeet(float now)
        {
            PlayerAgent agent = player.Agent;
            Vector3 velocity = agent.Movement.Velocity;
            float speed = new Vector2(velocity.x, velocity.z).magnitude;
            bool lockable = animationController != null && animationController.FeetFree && speed < plantMaxSpeed;

            int front = -1;
            SwingPhase phase = agent.Shot.Phase;
            bool shotComing = (phase == SwingPhase.Preparing || phase == SwingPhase.Swinging)
                              && !float.IsNegativeInfinity(contactTime) && now - contactTime > -stepInBefore;
            bool followingThrough = now - lastHitAt < stepInAfter;
            // A right-hander steps in with the left foot on the forehand and the right on the backhand, and stays on it through the follow-through.
            if (lockable && agent.Serve.Phase == ServePhase.Idle && (shotComing || followingThrough))
                front = agent.Shot.DisplaySide == HandSide.Forehand ? 0 : 1;

            for (int i = 0; i < 2; i++)
            {
                FootState foot = footStates[i];
                FootState other = footStates[1 - i];
                Vector3 desired = feet[i].position;
                Quaternion desiredRotation = feet[i].rotation;
                if (i == front) desired += Vector3.ProjectOnPlane(transform.forward, Vector3.up) * stepInDistance;
                bool grounded = feet[i].position.y - transform.position.y < footGround + 0.05f;

                if (!lockable || !grounded)
                {
                    // Let go smoothly: the clip takes the foot back over a moment rather than at once.
                    if (foot.planted) foot.release = 0f;
                    foot.planted = false;
                    foot.stepping = false;
                    foot.release = Mathf.MoveTowards(foot.release, 1f, Time.deltaTime / releaseTime);
                    foot.target = Vector3.Lerp(foot.position, desired, foot.release);
                    foot.targetRotation = Quaternion.Slerp(foot.rotation, desiredRotation, foot.release);
                    continue;
                }

                Vector3 flatOffset = Vector3.ProjectOnPlane(desired - foot.position, Vector3.up);
                if (!foot.planted || flatOffset.magnitude > TeleportDistance)
                {
                    // Planted where the foot is now, so taking over makes no jump.
                    foot.planted = true;
                    foot.stepping = false;
                    foot.position = foot.release < 1f ? foot.target : desired;
                    foot.rotation = foot.release < 1f ? foot.targetRotation : desiredRotation;
                    foot.release = 0f;
                }

                if (foot.stepping)
                {
                    foot.progress += Time.deltaTime / stepDuration;
                    float s = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(foot.progress));
                    foot.target = Vector3.Lerp(foot.from, desired, s) + Vector3.up * (Mathf.Sin(Mathf.PI * s) * stepHeight);
                    foot.targetRotation = Quaternion.Slerp(foot.fromRotation, desiredRotation, s);
                    if (foot.progress >= 1f)
                    {
                        foot.stepping = false;
                        foot.position = desired;
                        foot.rotation = desiredRotation;
                    }
                    continue;
                }

                bool drifted = flatOffset.magnitude > stepTrigger || Quaternion.Angle(foot.rotation, desiredRotation) > stepTurn;
                if (drifted && !other.stepping)
                {
                    foot.stepping = true;
                    foot.progress = 0f;
                    foot.from = foot.position;
                    foot.fromRotation = foot.rotation;
                }
                foot.target = new Vector3(foot.position.x, desired.y, foot.position.z);
                foot.targetRotation = foot.rotation;
            }
            return front;
        }

        /// <summary>
        /// Moves the hand so the strings meet the ball, keeping the racket's angle from the clip. A ball further than
        /// the arm alone can comfortably cover is reached by leaning the body towards it first.
        /// </summary>
        void ReachForBall(float weight)
        {
            Vector3 needed = contactPoint - RacketHead;
            neededThisFrame = needed.magnitude;
            float excess = needed.magnitude - armReach;
            if (excess > 0f)
            {
                // Lean towards the ball across the ground only: a low ball is met with the knees, keeping the back upright.
                Vector3 towards = new Vector3(needed.x, 0f, needed.z);
                Vector3 axis = Vector3.Cross(Vector3.up, towards);
                if (axis.sqrMagnitude > 1e-6f)
                {
                    float angle = Mathf.Min(excess * leanPerMetre, maxLean) * weight;
                    spine.rotation = Quaternion.AngleAxis(angle, axis.normalized) * spine.rotation;
                }
            }

            Vector3 offset = contactPoint - RacketHead;
            if (offset.magnitude > maxReach) offset = offset.normalized * maxReach;
            if (offset.sqrMagnitude < 1e-6f) return;

            Quaternion handRotation = hand.rotation;
            Vector3 elbowHint = lowerArm.position + (lowerArm.position - (upperArm.position + hand.position) * 0.5f).normalized * 0.3f;
            TwoBone(upperArm, lowerArm, hand, hand.position + offset * weight, elbowHint);
            hand.rotation = handRotation;

            // With the arm at full stretch, the wrist turns the racket head the last bit towards the ball.
            Vector3 wristToHead = RacketHead - hand.position;
            Vector3 wristToBall = contactPoint - hand.position;
            if (wristToHead.sqrMagnitude > 1e-4f && wristToBall.sqrMagnitude > 1e-4f)
            {
                Quaternion turn = Quaternion.FromToRotation(wristToHead, wristToBall);
                turn.ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180f) angle -= 360f;
                angle = Mathf.Clamp(angle, -maxWristTurn, maxWristTurn) * weight;
                hand.rotation = Quaternion.AngleAxis(angle, axis) * hand.rotation;
            }
        }

        /// <summary>
        /// Turns the head from where the clip points it towards the ball, smoothly. Turning sideways and nodding have
        /// their own limits, as a neck does: a low ball is followed mostly with the eyes, not by burying the chin.
        /// </summary>
        void LookAtBall()
        {
            Vector3 face = head.rotation * headFaceLocal;
            AnimatedFace = face;
            Vector3 toBall = (ball.Position - head.position).normalized;
            bool inPlay = session.State == SessionState.Rally || session.State == SessionState.Toss;

            Quaternion desired = Quaternion.identity;
            if (inPlay)
            {
                // Yaw and pitch in the player's own frame.
                Vector3 faceLocal = transform.InverseTransformDirection(face);
                Vector3 ballLocal = transform.InverseTransformDirection(toBall);
                float faceYaw = Mathf.Atan2(faceLocal.x, faceLocal.z) * Mathf.Rad2Deg;
                float facePitch = Mathf.Asin(Mathf.Clamp(faceLocal.y, -1f, 1f)) * Mathf.Rad2Deg;
                float yaw = faceYaw + Mathf.Clamp(Mathf.DeltaAngle(faceYaw, Mathf.Atan2(ballLocal.x, ballLocal.z) * Mathf.Rad2Deg), -maxLookYaw, maxLookYaw) * lookWeight;
                float pitch = facePitch + Mathf.Clamp(Mathf.Asin(Mathf.Clamp(ballLocal.y, -1f, 1f)) * Mathf.Rad2Deg - facePitch, -maxLookDown, maxLookUp) * lookWeight;
                Vector3 lookLocal = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;
                desired = Quaternion.FromToRotation(face, transform.TransformDirection(lookLocal));
            }
            lookOffset = Quaternion.Slerp(lookOffset, desired, 1f - Mathf.Exp(-lookSpeed * Time.deltaTime));

            // The neck takes part of the turn and the head the rest, which together make the whole turn.
            Quaternion headRotation = head.rotation;
            neck.rotation = Quaternion.Slerp(Quaternion.identity, lookOffset, 0.4f) * neck.rotation;
            head.rotation = lookOffset * headRotation;
        }

        /// <summary>
        /// Two-bone IK in world space: rotates upper and lower so that end reaches target, bending towards hint.
        /// Bone lengths are taken from the current pose.
        /// </summary>
        static void TwoBone(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 hint)
        {
            Vector3 a = upper.position;
            Vector3 b = lower.position;
            Vector3 c = end.position;
            float upperLength = (b - a).magnitude;
            float lowerLength = (c - b).magnitude;
            Vector3 toTarget = target - a;
            float distance = Mathf.Clamp(toTarget.magnitude, 0.01f, (upperLength + lowerLength) * 0.999f);
            Vector3 direction = toTarget.normalized;

            Vector3 bendAxis = Vector3.Cross(direction, hint - a);
            if (bendAxis.sqrMagnitude < 1e-8f) bendAxis = Vector3.Cross(direction, b - a);
            if (bendAxis.sqrMagnitude < 1e-8f) return;
            bendAxis.Normalize();

            float cosAngle = (upperLength * upperLength + distance * distance - lowerLength * lowerLength) / (2f * upperLength * distance);
            float angle = Mathf.Acos(Mathf.Clamp(cosAngle, -1f, 1f)) * Mathf.Rad2Deg;
            Vector3 elbow = a + Quaternion.AngleAxis(angle, bendAxis) * direction * upperLength;

            upper.rotation = Quaternion.FromToRotation(b - a, elbow - a) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(end.position - lower.position, a + direction * distance - lower.position) * lower.rotation;
        }
    }
}
