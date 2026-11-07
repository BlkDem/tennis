using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using TennisSim.Simulation;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TennisSim.Tests
{
    /// <summary>Runs the real scene with both players on bots: the whole stack from session to camera, without a human.</summary>
    public class PrototypeSceneTests
    {
        const string SceneName = "Prototype";
        const string ScreenshotDirectory = "Logs/Screenshots";

        GameManager game;
        Renderer nearRacket;
        Renderer farRacket;
        int hits;
        int points;
        float racketDistanceTotal;
        bool nearHitSeen;
        bool serveSaved;

        static Renderer RacketOf(PlayerController player) =>
            player.GetComponentsInChildren<SkinnedMeshRenderer>().First(r => r.name == "Racket");

        void OnRallyEvent(RallyEvent e)
        {
            if (e.Type == RallyEventType.Hit)
            {
                hits++;
                Renderer racket = e.Slot == PlayerSlot.Near ? nearRacket : farRacket;
                racketDistanceTotal += Vector3.Distance(racket.bounds.center, e.Point);
                if (e.Slot == PlayerSlot.Near && hits > 2) nearHitSeen = true;
                if (e.Slot == PlayerSlot.Near && !serveSaved && game.Session.RallyHits == 1)
                {
                    serveSaved = true;
                    SaveCloseUp(game.nearPlayer.Position, "serve.png");
                }
            }
            if (e.Type == RallyEventType.PointEnded) points++;
        }

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync(SceneName);
            game = Object.FindAnyObjectByType<GameManager>();
            Assert.That(game, Is.Not.Null, "The scene has no GameManager.");
            game.UseBotForNearPlayer();
            nearRacket = RacketOf(game.nearPlayer);
            farRacket = RacketOf(game.farPlayer);
            hits = points = 0;
            racketDistanceTotal = 0f;
            nearHitSeen = false;
            serveSaved = false;
            GameEvents.RallyEvent += OnRallyEvent;
            Time.timeScale = 3f;
        }

        [TearDown]
        public void Restore()
        {
            Time.timeScale = 1f;
            GameEvents.RallyEvent -= OnRallyEvent;
        }

        [UnityTest]
        public IEnumerator Scene_PlaysRalliesAndKeepsTheBallOnCamera()
        {
            Camera camera = Camera.main;
            Assert.That(camera, Is.Not.Null, "The scene has no main camera.");
            BallController ball = Object.FindAnyObjectByType<BallController>();
            PlayerController nearPlayer = game.nearPlayer;
            int feetVisibleFrames = 0;
            bool savedContact = false;

            yield return null;
            yield return null;
            Assert.That(nearRacket.bounds.center.z, Is.GreaterThan(nearPlayer.Position.z), "the near model must face the net");
            Assert.That(farRacket.bounds.center.z, Is.LessThan(game.farPlayer.Position.z), "the far model must face the net");

            float start = Time.time;
            int inPlayFrames = 0;
            int visibleFrames = 0;
            bool savedScreenshot = false;

            // Long rallies are normal, so keep watching until a point has finished, within reason.
            while (Time.time - start < 45f || (points == 0 && Time.time - start < 240f))
            {
                yield return null;

                if (game.Session.State != SessionState.Rally) continue;

                inPlayFrames++;
                Vector3 viewport = camera.WorldToViewportPoint(ball.Position);
                bool visible = viewport.z > 0f && viewport.x is > 0f and < 1f && viewport.y is > 0f and < 1f;
                if (visible) visibleFrames++;

                if (!savedContact && nearHitSeen)
                {
                    savedContact = true;
                    SaveCloseUp(nearPlayer.Position, "contact.png");
                }

                Vector3 feet = camera.WorldToViewportPoint(nearPlayer.Position);
                if (feet.y > 0f && feet.y < 1f && feet.x > 0f && feet.x < 1f) feetVisibleFrames++;

                if (!savedScreenshot && hits >= 4)
                {
                    savedScreenshot = true;
                    SaveScreenshot(camera, "rally.png");
                }
            }

            Debug.Log($"Scene test: hits {hits}, points {points}, ball on screen {visibleFrames}/{inPlayFrames} frames, near player's feet on screen {feetVisibleFrames}/{inPlayFrames} frames");
            Assert.That(feetVisibleFrames / (float)inPlayFrames, Is.GreaterThan(0.95f), "the camera should keep the near player in view");
            float racketDistance = racketDistanceTotal / Mathf.Max(hits, 1);
            Debug.Log($"Scene test: racket centre is {racketDistance:F2} m from the ball at contact on average");
            Assert.That(hits, Is.GreaterThan(10), "the players should be hitting the ball");
            Assert.That(racketDistance, Is.LessThan(1.1f), "the swing animation should bring the racket to the ball");
            Assert.That(points, Is.GreaterThan(0), "points should finish");
            Assert.That(visibleFrames / (float)inPlayFrames, Is.GreaterThan(0.97f), "the camera should keep the ball in view");
        }

        /// <summary>A close view of the player at a hit, for judging the swing by eye.</summary>
        static void SaveCloseUp(Vector3 playerPosition, string fileName)
        {
            var cameraObject = new GameObject("Close-up Camera");
            var closeUp = cameraObject.AddComponent<Camera>();
            closeUp.clearFlags = CameraClearFlags.SolidColor;
            closeUp.backgroundColor = new Color(0.56f, 0.72f, 0.88f);
            cameraObject.transform.position = playerPosition + new Vector3(-1.5f, 1.5f, 3.5f);
            cameraObject.transform.LookAt(playerPosition + Vector3.up);
            SaveScreenshot(closeUp, fileName);
            Object.Destroy(cameraObject);
        }

        static void SaveScreenshot(Camera camera, string fileName)
        {
            var target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;

            camera.targetTexture = target;
            camera.Render(); // In batch mode nothing has drawn yet and the first frame comes out blank.
            camera.Render();
            RenderTexture.active = target;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();

            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;

            Directory.CreateDirectory(ScreenshotDirectory);
            File.WriteAllBytes(Path.Combine(ScreenshotDirectory, fileName), image.EncodeToPNG());
            Object.Destroy(image);
            Object.Destroy(target);
        }
    }
}
