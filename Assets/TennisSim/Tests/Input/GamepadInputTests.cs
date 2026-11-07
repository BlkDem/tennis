using NUnit.Framework;
using TennisSim.Simulation;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace TennisSim.Tests
{
    /// <summary>Drives the real action asset through virtual devices, so bindings are tested exactly as shipped.</summary>
    public class GamepadInputTests : InputTestFixture
    {
        const string AssetPath = "Assets/TennisSim/Settings/TennisControls.inputactions";

        InputActionAsset template;
        HumanIntentSource near;
        HumanIntentSource far;

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            template = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            Assert.That(template, Is.Not.Null, "The controls asset is missing.");
            near = new HumanIntentSource(template, InputManager.KeyboardP1Group);
            far = new HumanIntentSource(template, InputManager.KeyboardP2Group);
        }

        [TearDown]
        public override void TearDown()
        {
            near.Dispose();
            far.Dispose();
            base.TearDown();
        }

        static PlayerIntent Read(HumanIntentSource source) => source.ReadIntent(default);

        void AssertPressAndRelease(ButtonControl control, ShotButton expected)
        {
            Press(control);
            Assert.That(Read(near).Pressed, Is.EqualTo(expected), control.path);
            Release(control);
            Assert.That(Read(near).Released, Is.EqualTo(expected), control.path);
        }

        [Test]
        public void Gamepad_EveryShotHasAButton()
        {
            var pad = InputSystem.AddDevice<Gamepad>();
            near.SetDevices(pad);

            AssertPressAndRelease(pad.buttonSouth, ShotButton.Auto);
            AssertPressAndRelease(pad.buttonEast, ShotButton.Slice);
            AssertPressAndRelease(pad.buttonWest, ShotButton.Flat);
            AssertPressAndRelease(pad.buttonNorth, ShotButton.Lob);
            AssertPressAndRelease(pad.rightShoulder, ShotButton.Volley);
            AssertPressAndRelease(pad.leftShoulder, ShotButton.Drop);
            AssertPressAndRelease(pad.dpad.right, ShotButton.Forehand);
            AssertPressAndRelease(pad.dpad.left, ShotButton.Backhand);
        }

        [Test]
        public void Gamepad_SticksMoveAndAimAndTriggerSprints()
        {
            var pad = InputSystem.AddDevice<Gamepad>();
            near.SetDevices(pad);

            Set(pad.leftStick, new Vector2(1f, 0f));
            Set(pad.rightStick, new Vector2(0f, -1f));
            Press(pad.rightTrigger);

            PlayerIntent intent = Read(near);
            Assert.That(intent.Move.x, Is.GreaterThan(0.9f));
            Assert.That(intent.Aim.y, Is.LessThan(-0.9f));
            Assert.That(intent.Sprint, Is.True);

            Set(pad.leftStick, Vector2.zero);
            Release(pad.rightTrigger);
            intent = Read(near);
            Assert.That(intent.Move.magnitude, Is.LessThan(0.01f));
            Assert.That(intent.Sprint, Is.False);
        }

        [Test]
        public void Gamepad_SmallStickDriftIsIgnored()
        {
            var pad = InputSystem.AddDevice<Gamepad>();
            near.SetDevices(pad);
            Set(pad.leftStick, new Vector2(0.05f, 0.05f));
            Assert.That(Read(near).Move.magnitude, Is.LessThan(0.01f));
        }

        [Test]
        public void Gamepads_AreKeptApartBetweenPlayers()
        {
            var first = InputSystem.AddDevice<Gamepad>();
            var second = InputSystem.AddDevice<Gamepad>();
            near.SetDevices(first);
            far.SetDevices(second);

            Press(second.buttonSouth);
            Assert.That(Read(near).Pressed, Is.Null);
            Assert.That(Read(far).Pressed, Is.EqualTo(ShotButton.Auto));

            Set(first.leftStick, new Vector2(0f, 1f));
            Assert.That(Read(near).Move.y, Is.GreaterThan(0.9f));
            Assert.That(Read(far).Move.magnitude, Is.LessThan(0.01f));
        }

        [Test]
        public void Keyboard_LayoutsDoNotOverlap()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            near.SetDevices(keyboard);
            far.SetDevices(keyboard);

            Press(keyboard.spaceKey);
            Assert.That(Read(near).Pressed, Is.EqualTo(ShotButton.Auto));
            Assert.That(Read(far).Pressed, Is.Null);
            Release(keyboard.spaceKey);
            Read(near);

            Press(keyboard.numpad0Key);
            Assert.That(Read(far).Pressed, Is.EqualTo(ShotButton.Auto));
            Assert.That(Read(near).Pressed, Is.Null);
            Release(keyboard.numpad0Key);
            Read(far);

            Press(keyboard.wKey);
            Assert.That(Read(near).Move.y, Is.GreaterThan(0.9f));
            Assert.That(Read(far).Move.magnitude, Is.LessThan(0.01f));
        }

        [Test]
        public void QuickTap_IsNotLostBetweenTicks()
        {
            var pad = InputSystem.AddDevice<Gamepad>();
            near.SetDevices(pad);

            // Pressed and released between two gameplay ticks: both edges must still arrive, in order.
            Press(pad.buttonSouth);
            Release(pad.buttonSouth);

            Assert.That(Read(near).Pressed, Is.EqualTo(ShotButton.Auto));
            Assert.That(Read(near).Released, Is.EqualTo(ShotButton.Auto));
        }
    }
}
