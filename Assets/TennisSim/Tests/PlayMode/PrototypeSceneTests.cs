using System.Collections;
using System.IO;
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
        int hits;
        int points;

        void OnRallyEvent(RallyEvent e)
        {
            if (e.Type == RallyEventType.Hit) hits++;
            if (e.Type == RallyEventType.PointEnded) points++;
        }

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync(SceneName);
            game = Object.FindAnyObjectByType<GameManager>();
            Assert.That(game, Is.Not.Null, "The scene has no GameManager.");
            game.UseBotForNearPlayer();
            hits = points = 0;
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

            float start = Time.time;
            int inPlayFrames = 0;
            int visibleFrames = 0;
            bool savedScreenshot = false;

            while (Time.time - start < 45f)
            {
                yield return null;

                if (game.Session.State != SessionState.Rally) continue;

                inPlayFrames++;
                Vector3 viewport = camera.WorldToViewportPoint(ball.Position);
                bool visible = viewport.z > 0f && viewport.x is > 0f and < 1f && viewport.y is > 0f and < 1f;
                if (visible) visibleFrames++;

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
            Assert.That(hits, Is.GreaterThan(10), "the players should be hitting the ball");
            Assert.That(points, Is.GreaterThan(0), "points should finish");
            Assert.That(visibleFrames / (float)inPlayFrames, Is.GreaterThan(0.97f), "the camera should keep the ball in view");
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
