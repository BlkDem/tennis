using System;
using System.Collections.Generic;
using TennisSim.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TennisSim
{
    /// <summary>
    /// Reads one player's devices through its own copy of the action asset, so a later rebinding screen
    /// only has to edit that asset. Button edges are queued because input runs per frame and gameplay
    /// per tick: a quick tap must never fall between two ticks.
    /// </summary>
    public sealed class HumanIntentSource : IPlayerIntentSource, IDisposable
    {
        readonly InputActionAsset actions;
        readonly InputAction move;
        readonly InputAction aim;
        readonly InputAction sprint;
        readonly Queue<(ShotButton button, bool pressed)> edges = new Queue<(ShotButton, bool)>();
        /// <summary>The device this player last pressed a shot button or moved with; it decides how aiming works.</summary>
        InputDevice lastDevice;
        readonly List<(InputAction action, Action<InputAction.CallbackContext> onStarted, Action<InputAction.CallbackContext> onCanceled)> subscriptions =
            new List<(InputAction, Action<InputAction.CallbackContext>, Action<InputAction.CallbackContext>)>();

        public HumanIntentSource(InputActionAsset template, string keyboardGroup)
        {
            actions = UnityEngine.Object.Instantiate(template);
            actions.bindingMask = InputBinding.MaskByGroups(keyboardGroup, InputManager.GamepadGroup);

            InputActionMap map = actions.FindActionMap("Player", throwIfNotFound: true);
            move = map.FindAction("Move", throwIfNotFound: true);
            aim = map.FindAction("Aim", throwIfNotFound: true);
            sprint = map.FindAction("Sprint", throwIfNotFound: true);

            Bind(map, "ShotAuto", ShotButton.Auto);
            Bind(map, "ShotForehand", ShotButton.Forehand);
            Bind(map, "ShotBackhand", ShotButton.Backhand);
            Bind(map, "ShotSlice", ShotButton.Slice);
            Bind(map, "ShotFlat", ShotButton.Flat);
            Bind(map, "ShotVolley", ShotButton.Volley);
            Bind(map, "ShotLob", ShotButton.Lob);
            Bind(map, "ShotDrop", ShotButton.Drop);

            actions.Enable();
        }

        void Bind(InputActionMap map, string actionName, ShotButton button)
        {
            InputAction action = map.FindAction(actionName, throwIfNotFound: true);
            Action<InputAction.CallbackContext> onStarted = context =>
            {
                lastDevice = context.control?.device;
                edges.Enqueue((button, true));
            };
            Action<InputAction.CallbackContext> onCanceled = _ => edges.Enqueue((button, false));
            action.started += onStarted;
            action.canceled += onCanceled;
            subscriptions.Add((action, onStarted, onCanceled));
        }

        public void SetDevices(params InputDevice[] devices) => actions.devices = devices;

        /// <summary>Forgets queued presses, so the button that confirmed a menu does not also toss the first serve.</summary>
        public void Clear() => edges.Clear();

        /// <summary>This player's own copy of the controls, which rebinding edits.</summary>
        public InputActionAsset Actions => actions;

        public void ApplyOverrides(string json)
        {
            actions.RemoveAllBindingOverrides();
            if (!string.IsNullOrEmpty(json)) actions.LoadBindingOverridesFromJson(json);
        }

        public PlayerIntent ReadIntent(in PlayerPerception perception)
        {
            if (move.activeControl != null) lastDevice = move.activeControl.device;
            var intent = new PlayerIntent
            {
                Move = move.ReadValue<Vector2>(),
                Aim = aim.ReadValue<Vector2>(),
                Sprint = sprint.IsPressed(),
                // On a keyboard the same keys move and aim, so a held shot turns them into an aim cursor.
                AimCursor = !(lastDevice is Gamepad),
            };

            if (edges.Count > 0)
            {
                (ShotButton button, bool pressed) = edges.Dequeue();
                if (pressed) intent.Pressed = button;
                else intent.Released = button;
            }
            return intent;
        }

        public void Dispose()
        {
            foreach ((InputAction action, Action<InputAction.CallbackContext> onStarted, Action<InputAction.CallbackContext> onCanceled) in subscriptions)
            {
                action.started -= onStarted;
                action.canceled -= onCanceled;
            }
            subscriptions.Clear();
            actions.Disable();
            if (Application.isPlaying) UnityEngine.Object.Destroy(actions);
            else UnityEngine.Object.DestroyImmediate(actions);
        }
    }

    /// <summary>
    /// Owns the two human input slots and shares the devices between them: each has a keyboard layout,
    /// the first gamepad goes to the near player and the second to the far player. Handles hot-plugging.
    /// Runs before GameManager, which picks up the input slots in its own Awake.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class InputManager : MonoBehaviour
    {
        public const string KeyboardP1Group = "KeyboardP1";
        public const string KeyboardP2Group = "KeyboardP2";
        public const string GamepadGroup = "Gamepad";

        public InputActionAsset actions;

        public HumanIntentSource Near { get; private set; }
        public HumanIntentSource Far { get; private set; }
        public int GamepadCount => Gamepad.all.Count;

        /// <summary>Raised after a gamepad was plugged in or removed and devices were reassigned.</summary>
        public event Action DevicesChanged;

        void Awake()
        {
            Near = new HumanIntentSource(actions, KeyboardP1Group);
            Far = new HumanIntentSource(actions, KeyboardP2Group);
            ApplySavedBindings();
            AssignDevices();
        }

        /// <summary>
        /// The player's rebinds apply to both copies: player 1's keys only exist in the near player's bindings and
        /// player 2's in the far one's, while gamepad buttons are shared.
        /// </summary>
        public void ApplySavedBindings()
        {
            string json = GameSettings.BindingOverrides;
            Near.ApplyOverrides(json);
            Far.ApplyOverrides(json);
        }

        void OnEnable() => InputSystem.onDeviceChange += OnDeviceChange;
        void OnDisable() => InputSystem.onDeviceChange -= OnDeviceChange;

        void OnDestroy()
        {
            Near?.Dispose();
            Far?.Dispose();
        }

        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (!(device is Gamepad) && !(device is Keyboard)) return;
            if (change != InputDeviceChange.Added && change != InputDeviceChange.Removed &&
                change != InputDeviceChange.Reconnected && change != InputDeviceChange.Disconnected) return;

            AssignDevices();
            DevicesChanged?.Invoke();
        }

        void AssignDevices()
        {
            Near.SetDevices(DevicesFor(0));
            Far.SetDevices(DevicesFor(1));
        }

        static InputDevice[] DevicesFor(int gamepadIndex)
        {
            var devices = new List<InputDevice>(2);
            if (Keyboard.current != null) devices.Add(Keyboard.current);
            if (gamepadIndex < Gamepad.all.Count) devices.Add(Gamepad.all[gamepadIndex]);
            return devices.ToArray();
        }
    }
}
