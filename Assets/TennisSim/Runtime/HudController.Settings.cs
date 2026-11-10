using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TennisSim
{
    /// <summary>The settings screens: graphics, sound, window and resolution, and rebinding the controls.</summary>
    public sealed partial class HudController
    {
        static readonly string[] QualityChoices = { "Низкая", "Средняя", "Высокая" };
        static readonly string[] VolumeChoices = Enumerable.Range(0, 11).Select(i => $"{i * 10}%").ToArray();
        static readonly string[] ScreenChoices = { "Полный экран", "Окно" };

        static readonly (string label, string action)[] RebindableActions =
        {
            ("Удар (авто)", "ShotAuto"), ("Справа", "ShotForehand"), ("Слева", "ShotBackhand"), ("Резаный", "ShotSlice"),
            ("Плоский", "ShotFlat"), ("С лёта", "ShotVolley"), ("Свеча", "ShotLob"), ("Укороченный", "ShotDrop"), ("Ускорение", "Sprint"),
        };

        sealed class BindingCell
        {
            public InputAction Action;
            public int Binding;
            public string Device;
            public Text Label;
        }

        GameObject settings, controls;
        OptionRow qualityRow, effectsRow, crowdRow, screenRow, resolutionRow;
        Selectable controlsButton, controlsReset;
        GameObject returnTo;
        GameObject returnFocus;
        readonly List<BindingCell> bindingCells = new List<BindingCell>();
        InputActionRebindingExtensions.RebindingOperation rebinding;

        bool SettingsOpen => settings != null && (settings.activeSelf || controls.activeSelf);

        void BuildSettings()
        {
            settings = Group("Settings", canvasRoot);
            settings.SetActive(false);
            bool resolutions = GameSettings.ResolutionChoosable;
            float extra = resolutions ? 80f : 0f;
            RectTransform panel = Box("Settings Panel", settings.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(820f, 640f + extra), PanelColour, centred: true);
            Label(panel, "НАСТРОЙКИ", new Vector2(0f, -36f), new Vector2(820f, 70f), 52, Accent, TextAnchor.MiddleCenter, true, centred: true);

            qualityRow = Option(panel, "Графика", QualityChoices, (int)GameSettings.GraphicsQuality, new Vector2(0f, -130f));
            effectsRow = Option(panel, "Звуки игры", VolumeChoices, Mathf.RoundToInt(GameSettings.EffectsVolume * 10f), new Vector2(0f, -210f));
            crowdRow = Option(panel, "Трибуны", VolumeChoices, Mathf.RoundToInt(GameSettings.CrowdVolume * 10f), new Vector2(0f, -290f));
            screenRow = Option(panel, "Экран", ScreenChoices, GameSettings.Fullscreen ? 0 : 1, new Vector2(0f, -370f));
            qualityRow.Changed += i => GameSettings.GraphicsQuality = (GameSettings.Quality)i;
            effectsRow.Changed += i => GameSettings.EffectsVolume = i / 10f;
            crowdRow.Changed += i => GameSettings.CrowdVolume = i / 10f;
            screenRow.Changed += i => GameSettings.Fullscreen = i == 0;
            if (resolutions)
            {
                Vector2Int[] sizes = GameSettings.Resolutions;
                resolutionRow = Option(panel, "Разрешение", sizes.Select(s => $"{s.x}×{s.y}").ToArray(),
                    System.Array.IndexOf(sizes, GameSettings.Resolution), new Vector2(0f, -450f));
                resolutionRow.Changed += i => GameSettings.Resolution = sizes[i];
            }

            controlsButton = MenuButton(panel, "УПРАВЛЕНИЕ", new Vector2(0f, -460f - extra), OpenControls);
            Selectable back = MenuButton(panel, "НАЗАД", new Vector2(0f, -540f - extra), CloseSettings);
            Chain(qualityRow, effectsRow, crowdRow, screenRow, resolutionRow, controlsButton, back);
        }

        void BuildControls()
        {
            controls = Group("Controls", canvasRoot);
            controls.SetActive(false);
            const float RowHeight = 54f;
            float height = 150f + RebindableActions.Length * RowHeight + 200f;
            RectTransform panel = Box("Controls Panel", controls.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000f, height), PanelColour, centred: true);
            Label(panel, "УПРАВЛЕНИЕ", new Vector2(0f, -30f), new Vector2(1000f, 60f), 46, Accent, TextAnchor.MiddleCenter, true, centred: true);
            Label(panel, "Клавиатура", new Vector2(380f, -100f), new Vector2(270f, 36f), 24, Muted, TextAnchor.MiddleCenter);
            Label(panel, "Геймпад", new Vector2(680f, -100f), new Vector2(270f, 36f), 24, Muted, TextAnchor.MiddleCenter);

            InputActionAsset actions = game.inputManager.Near.Actions;
            var cells = new List<Selectable>();
            for (int row = 0; row < RebindableActions.Length; row++)
            {
                (string label, string actionName) = RebindableActions[row];
                InputAction action = actions.FindAction(actionName, throwIfNotFound: true);
                float y = -145f - row * RowHeight;
                Label(panel, label, new Vector2(50f, y), new Vector2(320f, RowHeight - 8f), 28, TextColour, TextAnchor.MiddleLeft);
                cells.Add(BindingButton(panel, action, InputManager.KeyboardP1Group, "<Keyboard>", new Vector2(380f, y)));
                cells.Add(BindingButton(panel, action, InputManager.GamepadGroup, "<Gamepad>", new Vector2(680f, y)));
            }

            float buttonsY = -150f - RebindableActions.Length * RowHeight - 10f;
            controlsReset = MenuButton(panel, "СБРОСИТЬ", new Vector2(0f, buttonsY), ResetBindings);
            MenuButton(panel, "НАЗАД", new Vector2(0f, buttonsY - 76f), CloseControls);
            Label(panel, "Нажмите на клавишу в таблице, затем новую клавишу. Esc или Start — отмена.",
                new Vector2(0f, buttonsY - 150f), new Vector2(1000f, 30f), 20, Muted, TextAnchor.MiddleCenter, centred: true);
            RefreshBindings();
        }

        Selectable BindingButton(RectTransform panel, InputAction action, string group, string device, Vector2 position)
        {
            int index = -1;
            for (int i = 0; i < action.bindings.Count; i++)
            {
                InputBinding binding = action.bindings[i];
                if (binding.isComposite || binding.isPartOfComposite || binding.groups == null) continue;
                if (binding.groups.Split(';').Contains(group))
                {
                    index = i;
                    break;
                }
            }

            RectTransform rect = Box(action.name + " " + group, panel, new Vector2(0f, 1f), position, new Vector2(270f, 46f), Color.white);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = rect.GetComponent<Image>();
            button.colors = Colours();
            Text label = NewText(rect, "", 26, TextColour, TextAnchor.MiddleCenter, true);
            Stretch(label.rectTransform);
            if (index < 0)
            {
                label.text = "—";
                button.interactable = false;
                return button;
            }

            var cell = new BindingCell { Action = action, Binding = index, Device = device, Label = label };
            bindingCells.Add(cell);
            button.onClick.AddListener(() => StartCoroutine(Rebind(cell)));
            return button;
        }

        void RefreshBindings()
        {
            foreach (BindingCell cell in bindingCells)
                cell.Label.text = cell.Action.GetBindingDisplayString(cell.Binding);
        }

        /// <summary>Waits for the confirming press to be let go, so it is not taken as the new binding, then listens for one.</summary>
        IEnumerator Rebind(BindingCell cell)
        {
            if (rebinding != null) yield break;
            cell.Label.text = "...";
            EventSystem.current.sendNavigationEvents = false;
            yield return new WaitForSecondsRealtime(0.25f);

            bool wasEnabled = cell.Action.enabled;
            cell.Action.Disable();
            rebinding = cell.Action.PerformInteractiveRebinding(cell.Binding)
                .WithControlsHavingToMatchPath(cell.Device)
                .WithControlsExcluding("<Mouse>")
                .WithCancelingThrough("<Keyboard>/escape")
                .WithCancelingThrough("<Gamepad>/start")
                .OnComplete(_ => FinishRebind(cell, wasEnabled, true))
                .OnCancel(_ => FinishRebind(cell, wasEnabled, false))
                .Start();
        }

        void FinishRebind(BindingCell cell, bool wasEnabled, bool changed)
        {
            // The operation is still running its own callback: it is disposed once that has returned.
            StartCoroutine(DisposeLater(rebinding));
            rebinding = null;
            if (wasEnabled) cell.Action.Enable();
            if (changed)
            {
                GameSettings.BindingOverrides = game.inputManager.Near.Actions.SaveBindingOverridesAsJson();
                GameSettings.Save();
                game.inputManager.ApplySavedBindings();
            }
            RefreshBindings();
            StartCoroutine(RestoreNavigation());
        }

        static IEnumerator DisposeLater(InputActionRebindingExtensions.RebindingOperation operation)
        {
            yield return null;
            operation?.Dispose();
        }

        /// <summary>An unfinished rebind is abandoned with the interface, rather than completing into a scene that is gone.</summary>
        void OnDestroy()
        {
            if (rebinding == null) return;
            rebinding.Dispose();
            rebinding = null;
        }

        /// <summary>The key that ended the rebind must not also move through the menu.</summary>
        static IEnumerator RestoreNavigation()
        {
            yield return new WaitForSecondsRealtime(0.2f);
            EventSystem.current.sendNavigationEvents = true;
        }

        void ResetBindings()
        {
            GameSettings.BindingOverrides = "";
            GameSettings.Save();
            game.inputManager.ApplySavedBindings();
            RefreshBindings();
        }

        // ---------------------------------------------------------------- Opening and closing

        void OpenSettings(GameObject from, GameObject focusBack)
        {
            returnTo = from;
            returnFocus = focusBack;
            from.SetActive(false);
            settings.SetActive(true);
            game.PauseKeysBlocked = true;
            EventSystem.current.SetSelectedGameObject(qualityRow.gameObject);
        }

        void CloseSettings()
        {
            GameSettings.Save();
            settings.SetActive(false);
            game.PauseKeysBlocked = false;
            if (returnTo != null) returnTo.SetActive(true);
            EventSystem.current.SetSelectedGameObject(returnFocus);
        }

        void OpenControls()
        {
            settings.SetActive(false);
            controls.SetActive(true);
            RefreshBindings();
            EventSystem.current.SetSelectedGameObject(controlsReset.gameObject);
        }

        void CloseControls()
        {
            controls.SetActive(false);
            settings.SetActive(true);
            EventSystem.current.SetSelectedGameObject(controlsButton.gameObject);
        }

        /// <summary>Esc, Start or B steps back out of the settings, unless a key is being rebound.</summary>
        void UpdateSettingsKeys()
        {
            if (!SettingsOpen || rebinding != null || !EventSystem.current.sendNavigationEvents) return;
            Keyboard keyboard = Keyboard.current;
            Gamepad gamepad = Gamepad.current;
            bool back = (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                        || (gamepad != null && (gamepad.startButton.wasPressedThisFrame || gamepad.buttonEast.wasPressedThisFrame));
            if (!back) return;
            if (controls.activeSelf) CloseControls();
            else CloseSettings();
        }
    }
}
