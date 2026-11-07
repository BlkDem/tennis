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
    /// Frame strips of real shots in the running game, filmed from the side, for judging the animation by eye.
    /// Also measures how close the racket's strings come to the ball at contact.
    /// </summary>
    public class AnimationCaptureTests
    {
        const string SceneName = "Prototype";
        const int Frames = 12;
        const float FrameStep = 1f / 30f;
        const int TileWidth = 300;
        const int TileHeight = 380;

        GameManager game;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            if (Directory.Exists(TestScreenshots.Directory))
                foreach (string file in Directory.GetFiles(TestScreenshots.Directory, "anim_*.png")) File.Delete(file);
            yield return SceneManager.LoadSceneAsync(SceneName);
            game = Object.FindAnyObjectByType<GameManager>();
            game.StartMatch(new MatchSetup { Rules = MatchRules.BestOfThree, Level = AiLevel.Medium, NearIsAi = true });
        }

        [TearDown]
        public void Restore() => Time.captureDeltaTime = 0f;

        [UnityTest, Timeout(300000)]
        public IEnumerator Capture_ShotsFromTheSide()
        {
            // Fixed frame steps, so every strip has the same timing whatever the machine.
            Time.captureDeltaTime = FrameStep;
            PlayerAgent near = game.Session.Agent(PlayerSlot.Near);
            if (System.Environment.GetEnvironmentVariable("TENNIS_NO_LOOK") == "1") game.nearPlayer.GetComponent<PlayerIkRig>().lookWeight = 0f;
            var cameraObject = new GameObject("Side Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.6f, 0.65f);
            camera.fieldOfView = 40f;

            int shots = 0;
            bool servedFilmed = false;
            float start = Time.time;
            while (shots < 6 && Time.time - start < 240f)
            {
                yield return null;
                // Filmed from a little before the contact: the shot is about to be struck.
                bool serveComing = !servedFilmed && near.Serve.Phase == ServePhase.Tossing;
                bool shotComing = near.Shot.Phase == SwingPhase.Preparing && near.Shot.Plan.Valid
                    && near.Shot.Plan.Time - game.Session.Time < 0.3f && near.Shot.Plan.Time > game.Session.Time;
                if (serveComing)
                {
                    servedFilmed = true;
                    yield return CaptureStrip(camera, game.nearPlayer, "anim_serve.png", 30, 2);
                }
                else if (shotComing)
                {
                    yield return CaptureStrip(camera, game.nearPlayer, $"anim_shot_{shots}.png", Frames, 1);
                    shots++;
                }
            }

            Object.Destroy(cameraObject);
            Assert.That(shots, Is.GreaterThan(0), "no rally shot was filmed");

            PlayerIkRig rig = game.nearPlayer.GetComponent<PlayerIkRig>();
            foreach (string note in rig.ContactNotes) Debug.Log("Animation reach: " + note);
            List<float> misses = rig.ContactMisses;
            Assert.That(misses.Count, Is.GreaterThan(0), "the racket never reached for a ball");
            float median = misses.OrderBy(m => m).ElementAt(misses.Count / 2);
            Debug.Log($"Animation: with IK the strings pass {median:F2} m from the contact point (median), worst {misses.Max():F2} m, over {misses.Count} reaches: {string.Join(" ", misses.Select(m => m.ToString("F2")))}");
            // The strings are about 0.25 m across: within 0.18 m of the centre, the ball is on the racket face.
            Assert.That(median, Is.LessThan(0.18f), "the strings should meet the ball");
        }

        /// <summary>Films the player from their right side, every <paramref name="every"/> frames.</summary>
        static IEnumerator CaptureStrip(Camera camera, PlayerController player, string fileName, int frames, int every)
        {
            int tiles = frames / every;
            int columns = 6;
            int rows = Mathf.CeilToInt(tiles / (float)columns);
            var sheet = new Texture2D(TileWidth * columns, TileHeight * rows, TextureFormat.RGB24, false);
            var target = new RenderTexture(TileWidth, TileHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            camera.targetTexture = target;

            Animator animator = player.GetComponentInChildren<Animator>();
            PlayerIkRig rig = player.GetComponent<PlayerIkRig>();
            Vector3 faceLocal = player.transform.InverseTransformDirection(rig.AnimatedFace);
            var log = new System.Text.StringBuilder($"{fileName}: face in player frame {faceLocal.x:F2} {faceLocal.y:F2} {faceLocal.z:F2} —");
            for (int i = 0; i < tiles; i++)
            {
                AnimatorStateInfo upper = animator.GetCurrentAnimatorStateInfo(1);
                string state = new[] { "Forehand_Prep", "Forehand_Swing", "Backhand_Prep", "Backhand_Swing", "ForehandSlice_Prep", "ForehandSlice_Swing",
                        "BackhandSlice_Prep", "BackhandSlice_Swing", "Volley_Prep", "Volley_Swing", "Smash_Prep", "Smash_Swing", "Neutral" }
                    .FirstOrDefault(upper.IsName) ?? "?";
                Vector3 final = player.transform.InverseTransformDirection(rig.FinalFace);
                log.Append($" [{i}: {player.Agent.Shot.Phase}, {state} {upper.normalizedTime:F2}, look {rig.LookAngle:F0}°, face {final.x:F2} {final.y:F2} {final.z:F2}]");
                Vector3 feet = player.Position;
                Vector3 side = player.transform.right;
                camera.transform.position = feet + side * 4.2f + Vector3.up * 1.3f + player.transform.forward * 0.6f;
                camera.transform.LookAt(feet + Vector3.up * 1.1f + player.transform.forward * 0.4f);
                camera.Render();
                RenderTexture.active = target;
                int column = i % columns;
                int row = rows - 1 - i / columns;
                sheet.ReadPixels(new Rect(0, 0, TileWidth, TileHeight), column * TileWidth, row * TileHeight);
                RenderTexture.active = null;
                for (int k = 0; k < every; k++) yield return null;
            }

            Debug.Log("Animation strip " + log);
            sheet.Apply();
            camera.targetTexture = null;
            Directory.CreateDirectory(TestScreenshots.Directory);
            File.WriteAllBytes(Path.Combine(TestScreenshots.Directory, fileName), sheet.EncodeToPNG());
            Object.Destroy(sheet);
            Object.Destroy(target);
        }
    }
}
