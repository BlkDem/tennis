using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TennisSim
{
    /// <summary>
    /// Offering a new version: at start the Windows game asks the server for its latest release, and if there is a newer
    /// one the menu offers it. The player decides; updating downloads it, closes the game and starts the new one.
    /// </summary>
    public sealed partial class HudController
    {
        GameObject updateDialog;
        Text updateText;
        Selectable updateButton, laterButton, closeButton;
        GameUpdater updater;
        bool updateOffered;

        bool UpdateOpen => updateDialog != null && updateDialog.activeSelf;

        void BuildUpdateDialog()
        {
            updateDialog = Group("Update", canvasRoot);
            updateDialog.SetActive(false);
            Image dim = updateDialog.AddComponent<Image>();
            dim.color = new Color(0f, 0f, 0f, 0.45f);
            RectTransform panel = Box("Update Panel", updateDialog.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(900f, 420f), PanelColour, centred: true);
            Label(panel, "ОБНОВЛЕНИЕ", new Vector2(0f, -36f), new Vector2(900f, 70f), 52, Accent, TextAnchor.MiddleCenter, true, centred: true);
            updateText = Label(panel, "", new Vector2(0f, -120f), new Vector2(820f, 110f), 28, TextColour, TextAnchor.MiddleCenter, centred: true);
            updateText.horizontalOverflow = HorizontalWrapMode.Wrap;
            updateButton = MenuButton(panel, "ОБНОВИТЬ", new Vector2(0f, -250f), () => StartCoroutine(InstallUpdate()));
            laterButton = MenuButton(panel, "ПОЗЖЕ", new Vector2(0f, -330f), CloseUpdate);
            closeButton = MenuButton(panel, "ЗАКРЫТЬ", new Vector2(0f, -330f), CloseUpdate);
            Chain(updateButton, laterButton);

            Label(menu.transform, "версия " + Application.version, Anchored(new Vector2(1f, 0f), new Vector2(-130f, 24f)), new Vector2(240f, 30f), 20, Muted, TextAnchor.MiddleCenter);
        }

        /// <summary>Asks the server once, at start; a game without network or in a browser just plays on.</summary>
        IEnumerator CheckForUpdate()
        {
            updater = GameUpdater.ForThisGame();
            if (updater == null) yield break;
            yield return updater.Check();
        }

        /// <summary>The offer waits for the menu, so it never interrupts a match.</summary>
        void UpdateUpdateDialog()
        {
            if (updater == null || game == null) return;
            if (!updateOffered && updater.State == UpdateState.Available && game.Phase == GamePhase.Menu && !SettingsOpen)
            {
                updateOffered = true;
                ShowUpdate($"Вышла новая версия {updater.Latest.version}, у вас {updater.CurrentVersion}.\nОбновить сейчас? Игра закроется и запустится заново.",
                    offer: true);
            }

            if (!UpdateOpen) return;
            if (updater.State == UpdateState.Downloading)
                Set(updateText, $"Загрузка версии {updater.Latest.version}… {Mathf.RoundToInt(updater.Progress * 100f)}%");

            // Esc, Start or B: later, or close a failure.
            Keyboard keyboard = Keyboard.current;
            Gamepad gamepad = Gamepad.current;
            bool back = (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                        || (gamepad != null && (gamepad.startButton.wasPressedThisFrame || gamepad.buttonEast.wasPressedThisFrame));
            if (back && updater.State != UpdateState.Downloading && updater.State != UpdateState.Ready) CloseUpdate();
        }

        void ShowUpdate(string text, bool offer)
        {
            Set(updateText, text);
            updateButton.gameObject.SetActive(offer);
            laterButton.gameObject.SetActive(offer);
            closeButton.gameObject.SetActive(!offer && updater.State == UpdateState.Failed);
            updateDialog.SetActive(true);
            game.PauseKeysBlocked = true;
            Refresh();
            GameObject focus = offer ? updateButton.gameObject : closeButton.gameObject.activeSelf ? closeButton.gameObject : null;
            EventSystem.current.SetSelectedGameObject(focus);
        }

        IEnumerator InstallUpdate()
        {
            ShowUpdate($"Загрузка версии {updater.Latest.version}…", offer: false);
            yield return updater.Download();
            if (updater.State == UpdateState.Ready)
            {
                Set(updateText, "Устанавливаю, игра сейчас перезапустится…");
                yield return new WaitForSecondsRealtime(0.5f);
                if (updater.StartInstaller(System.Diagnostics.Process.GetCurrentProcess().Id))
                {
                    game.Quit();
                    yield break;
                }
            }
            ShowUpdate($"Не удалось обновить: {updater.Error}.\nСкачать вручную: tennis.umolab.ru/download/windows", offer: false);
        }

        void CloseUpdate()
        {
            updateDialog.SetActive(false);
            game.PauseKeysBlocked = false;
            shownPhase = (GamePhase)(-1);
            Refresh();
        }
    }
}
