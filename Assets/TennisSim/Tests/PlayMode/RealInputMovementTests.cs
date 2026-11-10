using System.Collections;
using NUnit.Framework;
using TennisSim.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TennisSim.Tests
{
    /// <summary>
    /// The real scene driven by virtual devices through the live Input System: events take
    /// the same path as a player's hands, from the device through the action asset to the court.
    /// </summary>
    public class RealInputMovementTests
    {
        const string SceneName = "Prototype";

        GameManager game;
        PlayerAgent near;
        Keyboard keyboard;
        Gamepad addedGamepad;
        bool nearHit;
        bool farHit;
        ShotResult lastNearShot;
        InputSettings.BackgroundBehavior savedBackgroundBehavior;
        InputSettings.EditorInputBehaviorInPlayMode savedEditorBehavior;

        void OnRallyEvent(RallyEvent e)
        {
            if (e.Type != RallyEventType.Hit) return;
            if (e.Slot == PlayerSlot.Near)
            {
                nearHit = true;
                lastNearShot = e.Shot;
            }
            else farHit = true;
        }

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            // A batch-mode editor has no focused game view, which would otherwise swallow these devices.
            savedBackgroundBehavior = InputSystem.settings.backgroundBehavior;
            savedEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;

            keyboard = InputSystem.AddDevice<Keyboard>();
            keyboard.MakeCurrent();
            // The near player gets the first gamepad, which may be a real one on this machine.
            if (Gamepad.all.Count == 0) addedGamepad = InputSystem.AddDevice<Gamepad>();

            yield return SceneManager.LoadSceneAsync(SceneName);
            game = Object.FindAnyObjectByType<GameManager>();
            Assert.That(game, Is.Not.Null);
            game.StartMatch(MatchSetup.Default);
            near = game.Session.Agent(PlayerSlot.Near);
            nearHit = false;
            GameEvents.RallyEvent += OnRallyEvent;
        }

        [TearDown]
        public void RemoveDevices()
        {
            GameEvents.RallyEvent -= OnRallyEvent;
            InputSystem.RemoveDevice(keyboard);
            if (addedGamepad != null) InputSystem.RemoveDevice(addedGamepad);
            addedGamepad = null;
            InputSystem.settings.backgroundBehavior = savedBackgroundBehavior;
            InputSystem.settings.editorInputBehaviorInPlayMode = savedEditorBehavior;
        }

        /// <summary>Holds the button until the racket meets the ball, as a player learning the game would.</summary>
        IEnumerator WaitForServeHit()
        {
            Assert.That(game.Session.Server, Is.EqualTo(PlayerSlot.Near));
            float start = Time.time;
            while (!nearHit)
            {
                Assert.That(Time.time - start, Is.LessThan(4f), "the serve was never struck: the shot button did not reach the game");
                yield return null;
            }
        }

        static IEnumerator WaitSeconds(float seconds)
        {
            float end = Time.time + seconds;
            while (Time.time < end) yield return null;
        }

        [UnityTest]
        public IEnumerator Keyboard_WMovesThePlayerPastTheBaseline()
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Space));
            yield return WaitForServeHit();
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());

            float start = near.Position.z;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
            yield return WaitSeconds(0.9f);
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());

            Debug.Log($"Keyboard W: player from z={start:F2} to z={near.Position.z:F2}");
            Assert.That(start, Is.LessThan(-CourtGeometry.HalfLength + 1f), "the serve is played from the baseline");
            Assert.That(near.Position.z, Is.GreaterThan(-9f), "W must carry the player into the court");
        }

        /// <summary>
        /// Keyboard play end to end: a held shot brings out the aim cursor, which A and D move, and the power bar over the
        /// player; the ball goes where the cursor was. The same on the serve, during the toss.
        /// </summary>
        [UnityTest, Timeout(60000)]
        public IEnumerator Keyboard_HeldShotAimsWithTheCursorAndShowsItsPower()
        {
            Camera camera = Camera.main;
            Transform serveMarker = game.nearPlayer.GetComponent<AimCursorView>().marker;
            GameObject powerBar = System.Array.Find(Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                t => t.name == "Power 0")?.gameObject;
            Assert.That(powerBar, Is.Not.Null, "the HUD has no power bar");
            Assert.That(powerBar.activeSelf, Is.False, "no shot is held yet");

            // The serve: toss, then D during the toss moves the cursor towards the T. An ace or a fault leaves no ball
            // to play, so serve again: the near player serves the whole first game.
            float start;
            for (int serve = 0; serve < 4 && !farHit; serve++)
            {
                start = Time.time;
                while (game.Session.State != SessionState.AwaitingServe)
                {
                    Assert.That(Time.time - start, Is.LessThan(10f), "the next point never came");
                    yield return null;
                }
                nearHit = false;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Space));
                yield return WaitSeconds(0.3f);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Space, Key.D));
                yield return WaitSeconds(0.25f);
                Assert.That(near.AimingWithCursor, Is.True, "the toss brings out the cursor");
                Assert.That(serveMarker.gameObject.activeSelf, Is.True, "the cursor is drawn on the court");
                if (serve == 0) TestScreenshots.Save(camera, "keyboard_serve_aim.png", withInterface: true);
                yield return WaitForServeHit();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());

                start = Time.time;
                while (!farHit && game.Session.State != SessionState.AwaitingServe && Time.time - start < 6f) yield return null;
            }
            if (!farHit) Assert.Inconclusive("four serves in a row were not returned, so there was no ball to play");

            // The return: hold Space with A, so the cursor goes left while the player runs to the ball.
            nearHit = false;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Space, Key.A));
            yield return WaitSeconds(0.45f);
            if (!near.AimingWithCursor) Assert.Inconclusive("the return went out of reach before the cursor could be checked");
            Vector2 cursor = near.AimTarget;
            float charge = near.Shot.Charge;
            bool reachable = near.Shot.Plan.Valid && near.Shot.Plan.Reachable;
            Assert.That(serveMarker.gameObject.activeSelf, Is.True);
            Assert.That(powerBar.activeSelf, Is.True, "the power bar shows while the shot is held");
            TestScreenshots.Save(camera, "keyboard_shot_aim.png", withInterface: true);

            start = Time.time;
            while (!nearHit && Time.time - start < 3f) yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            Debug.Log($"Keyboard aim: cursor at {cursor}, charge {charge:F2}, shot aimed at {lastNearShot.AimedTarget}, {lastNearShot.Velocity.magnitude * 3.6f:F0} km/h");
            Assert.That(charge, Is.GreaterThan(0.3f), "holding charges the shot");
            if (!nearHit && !reachable) Assert.Inconclusive("the return could not be reached in time");
            Assert.That(nearHit, Is.True, "the player reached the ball by themselves while the keys aimed");
            Assert.That(lastNearShot.AimedTarget.x, Is.LessThan(-1.5f), "A moved the aim to the left");
        }

        [UnityTest]
        public IEnumerator Gamepad_StickMovesThePlayerPastTheBaseline()
        {
            Gamepad gamepad = Gamepad.all[0];
            InputSystem.QueueStateEvent(gamepad, new GamepadState(GamepadButton.South));
            yield return WaitForServeHit();
            InputSystem.QueueStateEvent(gamepad, new GamepadState());

            float start = near.Position.z;
            InputSystem.QueueStateEvent(gamepad, new GamepadState { leftStick = new Vector2(0f, 1f) });
            yield return WaitSeconds(0.9f);
            InputSystem.QueueStateEvent(gamepad, new GamepadState());

            Debug.Log($"Gamepad stick: player from z={start:F2} to z={near.Position.z:F2}");
            Assert.That(start, Is.LessThan(-CourtGeometry.HalfLength + 1f), "the serve is played from the baseline");
            Assert.That(near.Position.z, Is.GreaterThan(-9f), "the stick must carry the player into the court");
        }
    }
}
