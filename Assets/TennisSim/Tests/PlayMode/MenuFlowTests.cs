using System.Collections;
using System.Linq;
using NUnit.Framework;
using TennisSim.Simulation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TennisSim.Tests
{
    /// <summary>The menu, the pause and the match result, driven by a virtual gamepad through the real UI.</summary>
    public class MenuFlowTests
    {
        const string SceneName = "Prototype";

        GameManager game;
        Gamepad gamepad;
        bool addedGamepad;
        InputSettings.BackgroundBehavior savedBackgroundBehavior;
        InputSettings.EditorInputBehaviorInPlayMode savedEditorBehavior;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            savedBackgroundBehavior = InputSystem.settings.backgroundBehavior;
            savedEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;

            addedGamepad = Gamepad.all.Count == 0;
            gamepad = addedGamepad ? InputSystem.AddDevice<Gamepad>() : Gamepad.all[0];
            gamepad.MakeCurrent();

            yield return SceneManager.LoadSceneAsync(SceneName);
            game = Object.FindAnyObjectByType<GameManager>();
            Assert.That(game, Is.Not.Null);
            yield return null;
        }

        [TearDown]
        public void Restore()
        {
            Time.timeScale = 1f;
            Time.maximumDeltaTime = 1f / 3f;
            if (addedGamepad) InputSystem.RemoveDevice(gamepad);
            InputSystem.settings.backgroundBehavior = savedBackgroundBehavior;
            InputSystem.settings.editorInputBehaviorInPlayMode = savedEditorBehavior;
        }

        IEnumerator Tap(GamepadButton button)
        {
            InputSystem.QueueStateEvent(gamepad, new GamepadState(button));
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(gamepad, new GamepadState());
            yield return null;
            yield return null;
        }

        [UnityTest]
        public IEnumerator Gamepad_StartsAMatchFromTheMenuAndPausesIt()
        {
            Assert.That(game.Phase, Is.EqualTo(GamePhase.Menu), "the game opens on the menu");
            Assert.That(game.Session.Agent(PlayerSlot.Near).IntentSource, Is.InstanceOf<OpponentAI>(), "an AI match plays behind the menu");
            Assert.That(EventSystem.current.currentSelectedGameObject, Is.Not.Null, "the menu has focus for the gamepad");
            TestScreenshots.Save(Camera.main, "menu.png", withInterface: true);

            // Opponent, match length and time of day, then the play button.
            yield return Tap(GamepadButton.DpadDown);
            yield return Tap(GamepadButton.DpadDown);
            yield return Tap(GamepadButton.DpadDown);
            yield return Tap(GamepadButton.South);

            Assert.That(game.Phase, Is.EqualTo(GamePhase.Playing));
            Assert.That(game.IsHuman(PlayerSlot.Near), Is.True);
            Assert.That(game.IsHuman(PlayerSlot.Far), Is.False, "the default opponent is the AI");
            Assert.That(game.Session.State, Is.EqualTo(SessionState.AwaitingServe), "the button that started the match must not also toss the serve");
            TestScreenshots.Save(Camera.main, "hud.png", withInterface: true);

            yield return Tap(GamepadButton.Start);
            Assert.That(game.Phase, Is.EqualTo(GamePhase.Paused));
            Assert.That(Time.timeScale, Is.EqualTo(0f));
            TestScreenshots.Save(Camera.main, "pause.png", withInterface: true);
            yield return Tap(GamepadButton.Start);
            Assert.That(game.Phase, Is.EqualTo(GamePhase.Playing));
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator Evening_TurnsOnTheFloodlights()
        {
            EnvironmentController environment = Object.FindAnyObjectByType<EnvironmentController>();
            Assert.That(environment, Is.Not.Null);
            Assert.That(environment.floodlights.Length, Is.EqualTo(4));
            Assert.That(environment.floodlights.All(l => !l.enabled), Is.True, "no floodlights by day");

            game.StartMatch(new MatchSetup { Rules = MatchRules.OneSet, Level = AiLevel.Medium, NearIsAi = true, Evening = true });
            Assert.That(environment.Evening, Is.True);
            Assert.That(environment.floodlights.All(l => l.enabled), Is.True);
            Assert.That(environment.sun.intensity, Is.LessThan(0.5f));
            for (int i = 0; i < 120; i++) yield return null;
            TestScreenshots.Save(Camera.main, "evening.png", withInterface: true);

            game.ShowMenu();
            Assert.That(environment.Evening, Is.False, "the menu is back in daylight");
        }

        [UnityTest, Timeout(400000)]
        public IEnumerator Match_EndsWithTheResult()
        {
            game.StartMatch(new MatchSetup { Rules = new MatchRules { SetsToWin = 1, GamesPerSet = 1 }, Level = AiLevel.Hard, NearIsAi = true });
            // Fast-forward: many fixed ticks per frame.
            Time.maximumDeltaTime = 0.5f;
            Time.timeScale = 16f;
            float start = Time.realtimeSinceStartup;
            while (game.Phase != GamePhase.MatchOver)
            {
                Assert.That(Time.realtimeSinceStartup - start, Is.LessThan(360f), "a one-game set never finished");
                yield return null;
            }
            Time.timeScale = 1f;
            for (int i = 0; i < 60; i++) yield return null;
            TestScreenshots.Save(Camera.main, "result.png", withInterface: true);
            Assert.That(game.Session.Score.MatchWinner.HasValue, Is.True);
            Assert.That(EventSystem.current.currentSelectedGameObject, Is.Not.Null, "the result screen takes focus for the rematch");
        }
    }
}
