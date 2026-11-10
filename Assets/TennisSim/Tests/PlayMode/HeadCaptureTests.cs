using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using TennisSim.Simulation;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TennisSim.Tests
{
    /// <summary>
    /// Watches a player's feet after everything has moved them, the IK rig included: for each frame the player moves at
    /// a steady run, how fast the lower foot goes over the court, as a share of the player's speed, and how high it is.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class FootSlipRecorder : MonoBehaviour
    {
        public PlayerAgent Agent;
        public readonly List<(float speed, float ratio, float height)> Slips = new List<(float, float, float)>();
        public int Frames, Moving;
        Transform[] feet;
        Vector3[] last;

        void Start()
        {
            Transform[] bones = GetComponentsInChildren<Transform>();
            feet = new[] { System.Array.Find(bones, t => t.name == "L_Foot"), System.Array.Find(bones, t => t.name == "R_Foot") };
            last = new[] { feet[0].position, feet[1].position };
        }

        void LateUpdate()
        {
            if (feet == null || Agent == null) return;
            Frames++;
            float dt = Time.deltaTime;
            Vector3 velocity = Agent.Movement.Velocity;
            float speed = new Vector2(velocity.x, velocity.z).magnitude;
            int low = feet[0].position.y <= feet[1].position.y ? 0 : 1;
            Vector3 delta = feet[low].position - last[low];
            last[0] = feet[0].position;
            last[1] = feet[1].position;
            if (speed > 1f) Moving++;
            // A held shot plays on the upper body only, so the legs still run; a swing or a serve has its own feet.
            bool serving = Agent.Serve.Phase == ServePhase.Tossing || Agent.Serve.Phase == ServePhase.Swinging;
            bool steady = Agent.Shot.Phase != SwingPhase.Swinging && !serving && speed > 1f && dt > 0f;
            if (!steady || Mathf.Abs(delta.y) / dt > 0.25f) return;
            Slips.Add((speed, new Vector2(delta.x, delta.z).magnitude / dt / speed, feet[low].position.y - transform.position.y));
        }

        /// <summary>The frames whose lower foot is on the ground: at most a little above where the lowest tenth of them were.</summary>
        public List<(float speed, float ratio)> Grounded()
        {
            if (Slips.Count == 0) return new List<(float, float)>();
            float ground = Slips.Select(s => s.height).OrderBy(h => h).ElementAt(Slips.Count / 10);
            return Slips.Where(s => s.height < ground + 0.02f).Select(s => (s.speed, s.ratio)).ToList();
        }
    }

    /// <summary>Notes where the hips and feet are in the player's own space during a serve, after everything has moved them.</summary>
    [DefaultExecutionOrder(1000)]
    public class ServePoseProbe : MonoBehaviour
    {
        public PlayerAgent Agent;
        public readonly System.Text.StringBuilder Log = new System.Text.StringBuilder();
        Transform hips, left, right;

        void Start()
        {
            Transform[] bones = GetComponentsInChildren<Transform>();
            hips = System.Array.Find(bones, t => t.name == "Hips");
            left = System.Array.Find(bones, t => t.name == "L_Foot");
            right = System.Array.Find(bones, t => t.name == "R_Foot");
        }

        void LateUpdate()
        {
            if (Agent == null || hips == null || Agent.Serve.Phase == ServePhase.Idle || Log.Length > 4000) return;
            Vector3 h = transform.InverseTransformPoint(hips.position), l = transform.InverseTransformPoint(left.position), r = transform.InverseTransformPoint(right.position);
            Log.Append($"[{Agent.Serve.Phase} hips {h.x:F2},{h.y:F2},{h.z:F2} L {l.x:F2},{l.y:F2},{l.z:F2} R {r.x:F2},{r.y:F2},{r.z:F2}] ");
        }
    }

    [DefaultExecutionOrder(50)]
    public sealed class ClipServePoseProbe : ServePoseProbe
    {
    }

    /// <summary>Notes where the clip puts the feet, in the player's own space, before the IK rig moves them.</summary>
    [DefaultExecutionOrder(50)]
    public sealed class ClipFeetProbe : MonoBehaviour
    {
        public readonly Vector3[] Feet = new Vector3[2];
        Transform[] feet;

        void Start()
        {
            Transform[] bones = GetComponentsInChildren<Transform>();
            feet = new[] { System.Array.Find(bones, t => t.name == "L_Foot"), System.Array.Find(bones, t => t.name == "R_Foot") };
        }

        void LateUpdate()
        {
            for (int i = 0; i < 2; i++) Feet[i] = transform.InverseTransformPoint(feet[i].position);
        }
    }

    /// <summary>After the IK rig: how far it has taken each foot from where the clip put it, the most this frame.</summary>
    [DefaultExecutionOrder(1000)]
    public sealed class FeetDriftProbe : MonoBehaviour
    {
        public ClipFeetProbe Clip;
        public float Drift;
        Transform[] feet;

        void Start()
        {
            Transform[] bones = GetComponentsInChildren<Transform>();
            feet = new[] { System.Array.Find(bones, t => t.name == "L_Foot"), System.Array.Find(bones, t => t.name == "R_Foot") };
        }

        void LateUpdate()
        {
            Drift = 0f;
            for (int i = 0; i < 2; i++)
            {
                Vector3 offset = transform.InverseTransformPoint(feet[i].position) - Clip.Feet[i];
                Drift = Mathf.Max(Drift, new Vector2(offset.x, offset.z).magnitude);
            }
        }
    }

    /// <summary>The same, between the Animator and the IK rig: the clips' own feet, before the rig holds the landed ones.</summary>
    [DefaultExecutionOrder(50)]
    public sealed class ClipFootSlipRecorder : FootSlipRecorder
    {
    }

    /// <summary>
    /// Close-ups of the near player's head and shoulders through a rally, from behind as the game camera sees them and
    /// from the front, for judging the head's posture by eye. Also checks the head never sinks into the shoulders.
    /// </summary>
    public class HeadCaptureTests
    {
        const string SceneName = "Prototype";
        const int Tiles = 24;
        const int FramesBetween = 9;
        const int Tile = 220;
        const int Columns = 8;

        GameManager game;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            if (Directory.Exists(TestScreenshots.Directory))
                foreach (string file in Directory.GetFiles(TestScreenshots.Directory, "head_*.png")) File.Delete(file);
            yield return SceneManager.LoadSceneAsync(SceneName);
            game = Object.FindAnyObjectByType<GameManager>();
            game.StartMatch(new MatchSetup { Rules = MatchRules.BestOfThree, Level = AiLevel.Medium, NearIsAi = true });
        }

        [TearDown]
        public void Restore() => Time.captureDeltaTime = 0f;

        [UnityTest, Timeout(200000)]
        public IEnumerator Capture_HeadThroughARally()
        {
            Time.captureDeltaTime = 1f / 30f;
            PlayerController player = game.nearPlayer;
            PlayerIkRig rig = player.GetComponent<PlayerIkRig>();
            if (System.Environment.GetEnvironmentVariable("TENNIS_NO_LOOK") == "1") rig.lookWeight = 0f;
            Transform[] bones = player.GetComponentsInChildren<Transform>();
            Transform head = System.Array.Find(bones, t => t.name == "Head");
            Transform neck = System.Array.Find(bones, t => t.name == "Neck");
            Transform leftShoulder = System.Array.Find(bones, t => t.name == "L_UpperArm");
            Transform rightShoulder = System.Array.Find(bones, t => t.name == "R_UpperArm");

            var cameraObject = new GameObject("Head Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.6f, 0.65f);
            camera.fieldOfView = 30f;
            var target = new RenderTexture(Tile, Tile, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
            int rows = Mathf.CeilToInt(Tiles / (float)Columns);
            var behind = new Texture2D(Tile * Columns, Tile * rows, TextureFormat.RGB24, false);
            var front = new Texture2D(Tile * Columns, Tile * rows, TextureFormat.RGB24, false);
            Camera gameCamera = Camera.main;

            yield return new WaitForSeconds(1f);
            var log = new System.Text.StringBuilder("Head strip:");
            float lowestNeck = float.PositiveInfinity;
            float highestFace = -1f, lowestFace = 1f;
            for (int i = 0; i < Tiles; i++)
            {
                Vector3 shoulders = (leftShoulder.position + rightShoulder.position) * 0.5f;
                // How far the head stands above the shoulder line, along the body's up: low means sunk into the shoulders.
                float neckHeight = Vector3.Dot(head.position - shoulders, player.transform.up);
                lowestNeck = Mathf.Min(lowestNeck, neckHeight);
                Vector3 face = player.transform.InverseTransformDirection(rig.FinalFace);
                Vector3 animated = player.transform.InverseTransformDirection(rig.AnimatedFace);
                // Between points the head may hang over a lost point; the posture checked is the one in play.
                if (game.Session.State != SessionState.PointOver)
                {
                    highestFace = Mathf.Max(highestFace, face.y);
                    lowestFace = Mathf.Min(lowestFace, face.y);
                }
                log.Append($" [{i}: {game.Session.State} {player.Agent.Shot.Phase} neck {neckHeight:F2} look {rig.LookAngle:F0}° anim {animated.x:F2} {animated.y:F2} {animated.z:F2} final {face.x:F2} {face.y:F2} {face.z:F2}]");

                Vector3 focus = (head.position + neck.position) * 0.5f + Vector3.down * 0.1f;
                Vector3 view = gameCamera != null ? gameCamera.transform.forward : player.transform.forward;
                camera.transform.position = focus - view * 2.4f;
                camera.transform.LookAt(focus);
                Grab(camera, target, behind, i, rows);
                camera.transform.position = focus + player.transform.forward * 2.2f + player.transform.right * 0.6f + Vector3.up * 0.15f;
                camera.transform.LookAt(focus);
                Grab(camera, target, front, i, rows);
                for (int k = 0; k < FramesBetween; k++) yield return null;
            }

            Debug.Log(log);
            float up = Mathf.Asin(highestFace) * Mathf.Rad2Deg, down = -Mathf.Asin(lowestFace) * Mathf.Rad2Deg;
            Debug.Log($"Head: lowest neck height above the shoulders {lowestNeck:F2} m, face up to {up:F0}° and down to {down:F0}°");
            Save(behind, "head_behind.png");
            Save(front, "head_front.png");
            camera.targetTexture = null;
            Object.Destroy(target);
            Object.Destroy(cameraObject);

            // Even watching a serve toss overhead, a face pointing straight up means the head has been thrown back
            // past what a neck does, as happened when the look's turns piled up frame after frame.
            Assert.That(up, Is.LessThan(75f), "the head is thrown back too far");
            Assert.That(down, Is.LessThan(50f), "the head is bent down too far");
            Assert.That(lowestNeck, Is.GreaterThan(0.08f), "the head has sunk into the shoulders");
        }

        /// <summary>
        /// The whole body through the near player's first serve, from behind as the game camera sees it, from the side
        /// and from the front: for judging the stance and the feet by eye.
        /// </summary>
        [UnityTest, Timeout(120000)]
        public IEnumerator Capture_ServeFullBody()
        {
            Time.captureDeltaTime = 1f / 30f;
            PlayerController player = game.nearPlayer;
            PlayerAgent agent = player.Agent;
            if (System.Environment.GetEnvironmentVariable("TENNIS_NO_IK") == "1") player.GetComponent<PlayerIkRig>().enabled = false;
            var cameraObject = new GameObject("Serve Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.6f, 0.65f);
            camera.fieldOfView = 34f;
            const int Width = 200, Height = 300, Frames = 16;
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
            var sheet = new Texture2D(Width * Frames, Height * 3, TextureFormat.RGB24, false);
            Camera gameCamera = Camera.main;

            // From the stance before the toss to the landing after the swing.
            float start = Time.time;
            while (game.Session.State != SessionState.AwaitingServe || game.Session.Server != PlayerSlot.Near)
            {
                Assert.That(Time.time - start, Is.LessThan(30f), "the near player never got to serve");
                yield return null;
            }
            yield return new WaitForSeconds(0.3f);
            var probe = player.gameObject.AddComponent<ServePoseProbe>();
            probe.Agent = agent;
            var clipProbe = player.gameObject.AddComponent<ClipServePoseProbe>();
            clipProbe.Agent = agent;
            for (int i = 0; i < Frames; i++)
            {
                Vector3 feet = player.Position;
                Vector3 body = feet + Vector3.up * 0.95f;
                Vector3 back = gameCamera != null ? Vector3.ProjectOnPlane(gameCamera.transform.forward, Vector3.up).normalized : Vector3.forward;
                Vector3[] eyes =
                {
                    gameCamera != null ? gameCamera.transform.position : body - back * 5.5f + Vector3.up * 1.6f,
                    body + Vector3.right * 5.5f + Vector3.up * 0.3f,
                    body + Vector3.forward * 5.5f + Vector3.up * 0.3f,
                };
                for (int row = 0; row < 3; row++)
                {
                    // The top row is the game camera's own view, closed in on the player.
                    camera.transform.position = eyes[row];
                    camera.fieldOfView = row == 0 ? 2f * Mathf.Atan(1.4f / Vector3.Distance(eyes[row], body)) * Mathf.Rad2Deg : 34f;
                    camera.transform.LookAt(body);
                    camera.Render();
                    RenderTexture.active = target;
                    sheet.ReadPixels(new Rect(0, 0, Width, Height), i * Width, (2 - row) * Height);
                    RenderTexture.active = null;
                }
                Debug.Log($"Serve frame {i}: {game.Session.State} serve {agent.Serve.Phase}");
                for (int k = 0; k < 4; k++) yield return null;
            }
            Debug.Log("Serve pose before IK: " + clipProbe.Log);
            Debug.Log("Serve pose after IK: " + probe.Log);
            Save(sheet, "serve_body.png");
            camera.targetTexture = null;
            Object.Destroy(target);
            Object.Destroy(cameraObject);
        }

        /// <summary>
        /// The legs through a rally, from behind as the game camera sees them and from the front. Measures how far each
        /// knee falls inside the line from hip to ankle: knees caving in towards each other read as knock-kneed.
        /// </summary>
        [UnityTest, Timeout(200000)]
        public IEnumerator Capture_LegsThroughARally()
        {
            Time.captureDeltaTime = 1f / 30f;
            PlayerController player = game.nearPlayer;
            if (System.Environment.GetEnvironmentVariable("TENNIS_NO_IK") == "1") player.GetComponent<PlayerIkRig>().enabled = false;
            Transform[] bones = player.GetComponentsInChildren<Transform>();
            Transform Bone(string name) => System.Array.Find(bones, t => t.name == name);
            Transform[] hips = { Bone("L_UpperLeg"), Bone("R_UpperLeg") };
            Transform[] knees = { Bone("L_LowerLeg"), Bone("R_LowerLeg") };
            Transform[] ankles = { Bone("L_Foot"), Bone("R_Foot") };

            var cameraObject = new GameObject("Legs Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.6f, 0.65f);
            camera.fieldOfView = 30f;
            var target = new RenderTexture(Tile, Tile, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
            int rows = Mathf.CeilToInt(Tiles / (float)Columns);
            var behind = new Texture2D(Tile * Columns, Tile * rows, TextureFormat.RGB24, false);
            var front = new Texture2D(Tile * Columns, Tile * rows, TextureFormat.RGB24, false);
            Camera gameCamera = Camera.main;

            yield return new WaitForSeconds(1f);
            var log = new System.Text.StringBuilder("Legs strip:");
            var inward = new System.Collections.Generic.List<float>();
            for (int i = 0; i < Tiles; i++)
            {
                log.Append($" [{i}: {game.Session.State} {player.Agent.Shot.Phase}");
                for (int leg = 0; leg < 2; leg++)
                {
                    // The knee's distance from the hip-ankle line towards the other leg, in the ground plane.
                    Vector3 hip = hips[leg].position, knee = knees[leg].position, ankle = ankles[leg].position;
                    Vector3 line = (ankle - hip).normalized;
                    Vector3 off = knee - hip - line * Vector3.Dot(knee - hip, line);
                    Vector3 towardsOther = Vector3.ProjectOnPlane(hips[1 - leg].position - hip, Vector3.up).normalized;
                    float caveIn = Vector3.Dot(off, towardsOther);
                    inward.Add(caveIn);
                    log.Append($" {(leg == 0 ? "L" : "R")} {caveIn:F2}");
                }
                log.Append("]");

                Vector3 focus = player.Position + Vector3.up * 0.75f;
                Vector3 view = gameCamera != null ? Vector3.ProjectOnPlane(gameCamera.transform.forward, Vector3.up).normalized : player.transform.forward;
                camera.transform.position = focus - view * 4.5f + Vector3.up * 0.9f;
                camera.transform.LookAt(focus);
                Grab(camera, target, behind, i, rows);
                camera.transform.position = focus + player.transform.forward * 4.5f + Vector3.up * 0.3f;
                camera.transform.LookAt(focus);
                Grab(camera, target, front, i, rows);
                for (int k = 0; k < FramesBetween; k++) yield return null;
            }

            Debug.Log(log);
            inward.Sort();
            Debug.Log($"Legs: knee inside the hip-ankle line, median {inward[inward.Count / 2]:F3} m, worst {inward[inward.Count - 1]:F3} m");
            Save(behind, "legs_behind.png");
            Save(front, "legs_front.png");
            camera.targetTexture = null;
            Object.Destroy(target);
            Object.Destroy(cameraObject);
        }

        /// <summary>
        /// The near player walking back to place between points, filmed from the side and from behind: the walk is the
        /// clip seen most calmly, so it has to look like a walk.
        /// </summary>
        [UnityTest, Timeout(200000)]
        public IEnumerator Capture_WalkBetweenPoints()
        {
            Time.captureDeltaTime = 1f / 30f;
            PlayerController player = game.nearPlayer;
            PlayerAgent agent = player.Agent;
            var cameraObject = new GameObject("Walk Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.6f, 0.65f);
            camera.fieldOfView = 30f;
            var target = new RenderTexture(Tile, Tile, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
            int rows = Mathf.CeilToInt(Tiles / (float)Columns);
            var side = new Texture2D(Tile * Columns, Tile * rows, TextureFormat.RGB24, false);
            var behind = new Texture2D(Tile * Columns, Tile * rows, TextureFormat.RGB24, false);

            // Waits for a walk long enough to film: a metre and a half to go at least.
            float start = Time.time;
            while (!agent.WalkTarget.HasValue || Vector3.Distance(agent.Position, agent.WalkTarget.Value) < 1.5f)
            {
                Assert.That(Time.time - start, Is.LessThan(150f), "the near player never walked back to place");
                yield return null;
            }
            var log = new System.Text.StringBuilder("Walk strip:");
            for (int i = 0; i < Tiles; i++)
            {
                Vector3 velocity = agent.Movement.Velocity;
                log.Append($" {new Vector2(velocity.x, velocity.z).magnitude:F1}");
                Vector3 heading = velocity.sqrMagnitude > 0.01f ? new Vector3(velocity.x, 0f, velocity.z).normalized : player.transform.forward;
                Vector3 focus = player.Position + Vector3.up * 0.9f;
                camera.transform.position = focus + Vector3.Cross(Vector3.up, heading) * 4.5f;
                camera.transform.LookAt(focus);
                Grab(camera, target, side, i, rows);
                camera.transform.position = focus - heading * 4.5f + Vector3.up * 0.6f;
                camera.transform.LookAt(focus);
                Grab(camera, target, behind, i, rows);
                for (int k = 0; k < 2; k++) yield return null;
            }
            Debug.Log(log + " m/s");
            Save(side, "walk_side.png");
            Save(behind, "walk_behind.png");
            camera.targetTexture = null;
            Object.Destroy(target);
            Object.Destroy(cameraObject);
        }

        /// <summary>A human's hands, scripted: the stick let go, the shot button pressed once the ball can be played and held.</summary>
        sealed class HoldingHuman : IPlayerIntentSource
        {
            int revision = -1;
            bool servePressed;

            public PlayerIntent ReadIntent(in PlayerPerception p)
            {
                var intent = new PlayerIntent { AimCursor = true };
                if (p.AwaitingServe)
                {
                    if (p.IsServer && !servePressed) intent.Pressed = ShotButton.Auto;
                    servePressed = p.IsServer;
                    return intent;
                }
                servePressed = false;
                if (p.CanHit && p.BallRevision != revision)
                {
                    revision = p.BallRevision;
                    intent.Pressed = ShotButton.Auto;
                }
                return intent;
            }
        }

        /// <summary>
        /// A human's rally with the help on, from the game camera's angle but close: how the player looks walking up to the
        /// ball and shuffling into place. Logs how far each foot is from under the hips.
        /// </summary>
        [UnityTest, Timeout(200000)]
        public IEnumerator Capture_AssistedRallyFromTheGameCamera()
        {
            Time.captureDeltaTime = 1f / 30f;
            PlayerController player = game.nearPlayer;
            game.StartMatch(new MatchSetup { Rules = MatchRules.BestOfThree, Level = AiLevel.Easy });
            PlayerAgent agent = game.Session.Agent(PlayerSlot.Near);
            agent.IntentSource = new HoldingHuman();
            Transform[] bones = player.GetComponentsInChildren<Transform>();
            Transform hips = System.Array.Find(bones, t => t.name == "Hips");
            Transform[] feet = { System.Array.Find(bones, t => t.name == "L_Foot"), System.Array.Find(bones, t => t.name == "R_Foot") };

            var cameraObject = new GameObject("Close Game Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.6f, 0.65f);
            camera.fieldOfView = 12f;
            var target = new RenderTexture(Tile, Tile, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;
            const int Count = 48;
            int rows = Mathf.CeilToInt(Count / (float)Columns);
            var sheet = new Texture2D(Tile * Columns, Tile * rows, TextureFormat.RGB24, false);
            Camera gameCamera = Camera.main;

            yield return new WaitForSeconds(2f);
            var log = new System.Text.StringBuilder("Assisted strip:");
            float widest = 0f;
            for (int i = 0; i < Count; i++)
            {
                Vector3 velocity = agent.Movement.Velocity;
                float spread = 0f;
                foreach (Transform foot in feet)
                    spread = Mathf.Max(spread, Vector3.ProjectOnPlane(foot.position - hips.position, Vector3.up).magnitude);
                widest = Mathf.Max(widest, spread);
                log.Append($" [{i}: v {new Vector2(velocity.x, velocity.z).magnitude:F1} {agent.Shot.Phase}{(agent.Approach.Valid ? " approach" : "")} foot {spread:F2}]");

                Vector3 focus = player.Position + Vector3.up * 0.9f;
                camera.transform.position = gameCamera.transform.position;
                camera.transform.LookAt(focus);
                Grab(camera, target, sheet, i, rows);
                for (int k = 0; k < 3; k++) yield return null;
            }
            Debug.Log(log);
            Debug.Log($"Assisted: a foot is at most {widest:F2} m from under the hips");
            Save(sheet, "assisted_game_camera.png");
            camera.targetTexture = null;
            Object.Destroy(target);
            Object.Destroy(cameraObject);
        }

        /// <summary>
        /// Pressing Play moves the player from wherever the menu's exhibition left them to the baseline in one frame. The
        /// feet the IK rig held to the court there must not drag the legs sideways at the new spot.
        /// </summary>
        [UnityTest, Timeout(60000)]
        public IEnumerator NewMatch_FeetStartWhereTheClipPutsThem()
        {
            game.ShowMenu();
            yield return new WaitForSeconds(6f);
            PlayerController player = game.nearPlayer;
            var clip = player.gameObject.AddComponent<ClipFeetProbe>();
            var drift = player.gameObject.AddComponent<FeetDriftProbe>();
            drift.Clip = clip;
            yield return null;

            game.StartMatch(MatchSetup.Default);
            float worst = 0f;
            float end = Time.time + 1.5f;
            while (Time.time < end)
            {
                yield return null;
                worst = Mathf.Max(worst, drift.Drift);
            }
            Debug.Log($"New match: the IK rig took a foot at most {worst:F2} m from where the clip put it");
            Assert.That(worst, Is.LessThan(0.15f), "the feet are dragged from where the last match left them");
        }

        /// <summary>
        /// A planted foot stays where it is on the court: over a stretch of play, the lower foot of a moving player, when
        /// it is on the ground and not rising or falling, must move far slower than the player does. Sliding feet read
        /// as skating.
        /// </summary>
        [UnityTest, Timeout(200000)]
        public IEnumerator Locomotion_PlantedFeetDoNotSlide()
        {
            PlayerController player = game.nearPlayer;
            PlayerAgent agent = player.Agent;
            // Frames of a fixed length, as a game runs, rather than the thousands a batch editor makes in a second.
            Time.captureDeltaTime = 1f / 60f;
            var recorder = player.gameObject.AddComponent<FootSlipRecorder>();
            recorder.Agent = agent;
            var clipRecorder = player.gameObject.AddComponent<ClipFootSlipRecorder>();
            clipRecorder.Agent = agent;
            yield return new WaitForSeconds(40f);
            Object.Destroy(recorder);
            Object.Destroy(clipRecorder);

            List<(float speed, float ratio)> slips = recorder.Grounded();
            List<(float speed, float ratio)> clipSlips = clipRecorder.Grounded();
            if (clipSlips.Count > 0)
                Debug.Log($"Feet: before the IK rig the planted foot moves at {clipSlips.Select(s => s.ratio).OrderBy(r => r).ElementAt(clipSlips.Count / 2):F2} of the player's speed");
            Assert.That(slips.Count, Is.GreaterThan(100), $"the player hardly ran: {recorder.Frames} frames, {recorder.Moving} moving");
            var bands = new System.Text.StringBuilder();
            foreach ((float from, float to) in new[] { (1f, 2.5f), (2.5f, 4.5f), (4.5f, 9f) })
            {
                var band = slips.Where(s => s.speed >= from && s.speed < to).Select(s => s.ratio).OrderBy(r => r).ToList();
                if (band.Count > 0) bands.Append($" {from:F1}-{to:F1} m/s: {band[band.Count / 2]:F2} over {band.Count} frames;");
            }
            List<float> all = slips.Select(s => s.ratio).OrderBy(r => r).ToList();
            float median = all[all.Count / 2];
            Debug.Log($"Feet: the lower foot moves at {median:F2} of the player's speed (median);{bands}");
            Assert.That(median, Is.LessThan(0.3f), "planted feet slide over the court");
        }

        /// <summary>
        /// Each locomotion clip on its own, sampled straight from the imported model: its planted foot must go back at the
        /// speed the clip was authored for, or no playback rate can keep the feet from sliding. Then the same through the
        /// Animator's blend of gaits and directions.
        /// </summary>
        [UnityTest]
        public IEnumerator Locomotion_EachClipMovesItsPlantedFootAtItsOwnSpeed()
        {
            Animator animator = game.nearPlayer.GetComponentInChildren<Animator>();
            animator.enabled = false;
            game.nearPlayer.GetComponent<PlayerIkRig>().enabled = false;
            yield return null;
            Transform root = animator.transform;
            Transform[] bones = root.GetComponentsInChildren<Transform>();
            Transform[] feet = { System.Array.Find(bones, t => t.name == "L_Foot"), System.Array.Find(bones, t => t.name == "R_Foot") };
            var report = new System.Text.StringBuilder("Clips:");
            var wrong = new List<string>();
            foreach ((string name, float authored) in new[]
                     {
                         ("Walk", 1.4f), ("WalkBackLeft", 1.4f), ("WalkRight", 1.4f), ("WalkLeft", 1.4f), ("Jog", 3.5f), ("JogRight", 3.5f),
                         ("Run", 6.2f), ("RunRight", 6.2f), ("RunLeft", 6.2f), ("RunBackLeft", 6.2f), ("WalkBackRight", 1.4f),
                     })
            {
                AnimationClip clip = animator.runtimeAnimatorController.animationClips.First(c => c.name == name);
                const int Samples = 120;
                var speeds = new List<float>();
                Vector3[] before = new Vector3[2];
                for (int i = 0; i <= Samples; i++)
                {
                    float t = clip.length * i / Samples;
                    clip.SampleAnimation(root.gameObject, t);
                    Vector3[] now = { root.InverseTransformPoint(feet[0].position), root.InverseTransformPoint(feet[1].position) };
                    if (i > 0)
                    {
                        int low = now[0].y <= now[1].y ? 0 : 1;
                        Vector3 d = now[low] - before[low];
                        if (Mathf.Abs(d.y) < 0.002f) speeds.Add(new Vector2(d.x, d.z).magnitude / (clip.length / Samples));
                    }
                    before = now;
                }
                speeds.Sort();
                float planted = speeds.Count > 0 ? speeds[speeds.Count / 2] : -1f;
                report.Append($" {name} {clip.length:F2} s: planted foot {planted:F2} m/s (authored {authored:F1});");
                if (Mathf.Abs(planted - authored) > authored * 0.1f) wrong.Add(name);
            }
            Debug.Log(report);
            Assert.That(wrong, Is.Empty, "these clips' planted feet do not go back at the clip's speed");

            // The same through the Animator's blend, held at one speed and direction, with the match stopped so no point
            // ends and no reaction takes over the legs. Between gaits and between directions the planted foot must
            // still keep pace: a blend that cancels the strides shows here as a foot standing still.
            game.enabled = false;
            game.nearPlayer.enabled = false;
            animator.enabled = true;
            var blend = new System.Text.StringBuilder("Blend:");
            var slow = new List<string>();
            foreach ((float speed, Vector2 dir) in new[]
                     {
                         (6.2f, Vector2.right), (3.5f, Vector2.left), (1.4f, Vector2.up), (7.8f, new Vector2(-0.83f, -0.55f)),
                         (3.5f, new Vector2(0.71f, 0.71f)), (2.3f, Vector2.right), (4.9f, Vector2.left), (6.2f, new Vector2(0.3f, -0.95f)),
                         (1.4f, new Vector2(-0.6f, -0.8f)), (6.2f, Vector2.down), (3.5f, Vector2.down), (1.4f, Vector2.down),
                     })
            {
                animator.SetBool("Engaged", true);
                animator.SetFloat("Speed", speed);
                float side = 1f;
                Vector2 shown = PlayerAnimationController.BlendDirection(dir.normalized, ref side);
                animator.SetFloat("DirX", shown.x);
                animator.SetFloat("DirZ", shown.y);
                animator.SetFloat("StrideRate", speed / PlayerAnimationController.BlendedGaitSpeed(speed));
                animator.Play("Locomotion", 0, 0f);
                Time.captureDeltaTime = 1f / 60f;
                for (int i = 0; i < 30; i++) yield return null;
                var speeds = new List<float>();
                Vector3[] before = { root.InverseTransformPoint(feet[0].position), root.InverseTransformPoint(feet[1].position) };
                for (int i = 0; i < 90; i++)
                {
                    yield return null;
                    Vector3[] now = { root.InverseTransformPoint(feet[0].position), root.InverseTransformPoint(feet[1].position) };
                    int low = now[0].y <= now[1].y ? 0 : 1;
                    Vector3 d = now[low] - before[low];
                    if (Mathf.Abs(d.y) < 0.004f) speeds.Add(Vector2.Dot(new Vector2(d.x, d.z), -dir.normalized) / Time.deltaTime);
                    before = now;
                }
                speeds.Sort();
                float planted = speeds.Count > 0 ? speeds[speeds.Count / 2] : 0f;
                string playing = string.Join(" + ", animator.GetCurrentAnimatorClipInfo(0).Select(c => $"{c.clip.name} {c.weight:F2}"));
                blend.Append($" {speed:F1} m/s ({dir.x:F2}, {dir.y:F2}): planted foot {planted:F2} m/s, {playing};");
                if (planted < speed * 0.85f) slow.Add($"{speed:F1} m/s ({dir.x:F2}, {dir.y:F2})");
            }
            Time.captureDeltaTime = 0f;
            Debug.Log(blend);
            Assert.That(slow, Is.Empty, "at these speeds and directions the blended strides fall behind the player");
        }

        static void Grab(Camera camera, RenderTexture target, Texture2D sheet, int i, int rows)
        {
            camera.Render();
            RenderTexture.active = target;
            sheet.ReadPixels(new Rect(0, 0, Tile, Tile), (i % Columns) * Tile, (rows - 1 - i / Columns) * Tile);
            RenderTexture.active = null;
        }

        static void Save(Texture2D sheet, string fileName)
        {
            sheet.Apply();
            Directory.CreateDirectory(TestScreenshots.Directory);
            File.WriteAllBytes(Path.Combine(TestScreenshots.Directory, fileName), sheet.EncodeToPNG());
            Object.Destroy(sheet);
        }
    }
}
