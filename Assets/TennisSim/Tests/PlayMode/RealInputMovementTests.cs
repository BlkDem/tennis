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
        InputSettings.BackgroundBehavior savedBackgroundBehavior;
        InputSettings.EditorInputBehaviorInPlayMode savedEditorBehavior;

        void OnRallyEvent(RallyEvent e)
        {
            if (e.Type == RallyEventType.Hit && e.Slot == PlayerSlot.Near) nearHit = true;
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
