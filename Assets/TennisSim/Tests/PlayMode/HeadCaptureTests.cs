using System.Collections;
using System.IO;
using NUnit.Framework;
using TennisSim.Simulation;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TennisSim.Tests
{
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
                highestFace = Mathf.Max(highestFace, face.y);
                lowestFace = Mathf.Min(lowestFace, face.y);
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
