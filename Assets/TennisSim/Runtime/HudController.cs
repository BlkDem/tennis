using System.Linq;
using TennisSim.Simulation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace TennisSim
{
    /// <summary>
    /// The game's interface, built in code at start: the main menu, the score panel, the umpire's calls,
    /// feedback on the human player's own shots, the serve hint, the pause menu and the match result.
    /// Everything works with a gamepad, the keyboard or the mouse.
    /// </summary>
    public sealed partial class HudController : MonoBehaviour
    {
        const float KmhPerMs = 3.6f;
        const float CallSeconds = 1.8f;
        const float FeedbackSeconds = 2.2f;

        static readonly Color PanelColour = new Color(0.03f, 0.06f, 0.09f, 0.88f);
        static readonly Color Accent = new Color(0.98f, 0.8f, 0.22f);
        static readonly Color TextColour = new Color(0.95f, 0.96f, 0.95f);
        static readonly Color Muted = new Color(0.65f, 0.7f, 0.72f);
        static readonly Color ButtonNormal = new Color(0.1f, 0.15f, 0.2f, 0.95f);
        static readonly Color ButtonSelected = new Color(0.12f, 0.48f, 0.3f, 1f);
        static readonly Color Perfect = new Color(0.45f, 0.95f, 0.5f);
        static readonly Color Good = new Color(0.98f, 0.85f, 0.3f);
        static readonly Color Poor = new Color(1f, 0.45f, 0.35f);

        static readonly string[] OpponentChoices = { "ИИ · лёгкий", "ИИ · средний", "ИИ · сильный", "Второй игрок" };
        static readonly string[] LengthChoices = { "Короткий сет до 4 геймов", "Один сет", "До двух выигранных сетов" };
        static readonly MatchRules[] LengthRules = { MatchRules.ShortSet, MatchRules.OneSet, MatchRules.BestOfThree };
        static readonly string[] TimeChoices = { "День", "Вечер, прожекторы" };

        public GameManager game;
        [Tooltip("Must contain Cyrillic: the web build has no system fonts to fall back on.")]
        public Font font;
        RectTransform canvasRoot;
        GameObject hud, menu, pause, result;
        readonly Text[] names = new Text[2];
        readonly Text[] sets = new Text[2];
        readonly Text[] games = new Text[2];
        readonly Text[] points = new Text[2];
        readonly Text[] serving = new Text[2];
        Text tieBreak, call, feedback, hint, resultTitle, resultScore;
        Canvas canvas;
        readonly RectTransform[] powerBars = new RectTransform[2];
        readonly RectTransform[] powerFills = new RectTransform[2];
        const float PowerBarWidth = 150f;
        const float PowerBarHeight = 16f;
        const float PowerBarInset = 3f;
        /// <summary>The bar floats this high above a player's feet, over the head.</summary>
        const float PowerBarHeightOnCourt = 2.2f;
        OptionRow opponentRow, lengthRow, timeRow;
        Selectable playButton, resumeButton, rematchButton;
        float callUntil, feedbackUntil;
        GamePhase shownPhase = (GamePhase)(-1);

        void Awake()
        {
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            EnsureEventSystem();
            BuildCanvas();
            BuildHud();
            BuildMenu();
            BuildPause();
            BuildResult();
            BuildSettings();
            BuildControls();
        }

        void OnEnable()
        {
            GameEvents.RallyEvent += OnRallyEvent;
            if (game != null) game.Changed += Refresh;
        }

        void OnDisable()
        {
            GameEvents.RallyEvent -= OnRallyEvent;
            if (game != null) game.Changed -= Refresh;
        }

        void Start() => Refresh();

        // ---------------------------------------------------------------- State

        void Refresh()
        {
            if (game == null) return;
            GamePhase phase = game.Phase;
            if (SettingsOpen && phase != GamePhase.Menu && phase != GamePhase.Paused)
            {
                settings.SetActive(false);
                controls.SetActive(false);
                game.PauseKeysBlocked = false;
            }
            menu.SetActive(phase == GamePhase.Menu && !SettingsOpen);
            hud.SetActive(phase != GamePhase.Menu);
            pause.SetActive(phase == GamePhase.Paused && !SettingsOpen);
            result.SetActive(phase == GamePhase.MatchOver);
            if (phase == GamePhase.MatchOver) FillResult();
            if (phase != shownPhase)
            {
                shownPhase = phase;
                callUntil = feedbackUntil = 0f;
                GameObject focus = phase == GamePhase.Menu ? opponentRow.gameObject
                    : phase == GamePhase.Paused ? resumeButton.gameObject
                    : phase == GamePhase.MatchOver ? rematchButton.gameObject
                    : null;
                // In play nothing is selected, so the shot buttons never press a menu button by accident.
                EventSystem.current.SetSelectedGameObject(focus);
            }
        }

        void Update()
        {
            UpdateSettingsKeys();
            if (game == null || game.Session == null || !hud.activeSelf) return;
            RallySession session = game.Session;
            TennisScore score = session.Score;

            for (int i = 0; i < 2; i++)
            {
                var slot = (PlayerSlot)i;
                Set(names[i], game.PlayerName(slot));
                Set(sets[i], score.Sets(slot).ToString());
                Set(games[i], score.Games(slot).ToString());
                Set(points[i], PointText(score, slot));
                Set(serving[i], session.Server == slot && !score.MatchOver ? "●" : "");
            }
            Set(tieBreak, score.IsTieBreak ? "ТАЙ-БРЕЙК" : "");

            bool humanServing = session.State == SessionState.AwaitingServe && game.IsHuman(session.Server) && game.Phase == GamePhase.Playing;
            Set(hint, humanServing
                ? (session.ServeNumber == 2 ? "Вторая подача.  " : "") + "Нажмите удар — подброс, отпустите, когда мяч опускается; пока он в воздухе, WASD / стик целят.   H / X — плоская,  U / B — резаная,  остальные — кик"
                : "");

            Fade(call, callUntil);
            Fade(feedback, feedbackUntil);
            UpdatePowerBars();
        }

        /// <summary>Over a human player's head while a shot is held: how far the hold has charged its power. Full turns green.</summary>
        void UpdatePowerBars()
        {
            Camera view = Camera.main;
            for (int i = 0; i < 2; i++)
            {
                var slot = (PlayerSlot)i;
                PlayerController player = slot == PlayerSlot.Near ? game.nearPlayer : game.farPlayer;
                PlayerAgent agent = player != null ? player.Agent : null;
                SwingPhase phase = agent != null ? agent.Shot.Phase : SwingPhase.Idle;
                bool show = view != null && agent != null && game.Phase == GamePhase.Playing && game.IsHuman(slot)
                            && (phase == SwingPhase.Preparing || phase == SwingPhase.Swinging);
                Vector3 screen = show ? view.WorldToScreenPoint(player.Position + Vector3.up * PowerBarHeightOnCourt) : Vector3.zero;
                show &= screen.z > 0f;

                RectTransform bar = powerBars[i];
                if (bar.gameObject.activeSelf != show) bar.gameObject.SetActive(show);
                if (!show) continue;
                bar.anchoredPosition = (Vector2)screen / canvas.scaleFactor;
                float charge = agent.Shot.Charge;
                powerFills[i].sizeDelta = new Vector2((PowerBarWidth - PowerBarInset * 2f) * charge, PowerBarHeight - PowerBarInset * 2f);
                powerFills[i].GetComponent<Image>().color = charge >= 1f ? Perfect : Accent;
            }
        }

        static void Fade(Text text, float until)
        {
            float left = until - Time.unscaledTime;
            Color c = text.color;
            c.a = Mathf.Clamp01(left / 0.4f);
            text.color = c;
        }

        static string PointText(TennisScore score, PlayerSlot slot)
        {
            if (score.IsTieBreak) return score.PointText(slot);
            if (score.IsDeuce) return "40";
            PlayerSlot? advantage = score.Advantage;
            if (advantage.HasValue) return advantage.Value == slot ? "AD" : "40";
            return TennisScore.PointCall(score.GamePoints(slot));
        }

        // ---------------------------------------------------------------- Events

        void OnRallyEvent(RallyEvent e)
        {
            if (game == null || game.Phase != GamePhase.Playing) return;
            switch (e.Type)
            {
                case RallyEventType.Hit:
                    if (game.IsHuman(e.Slot)) ShowFeedback(e.Shot);
                    break;
                case RallyEventType.Fault:
                    ShowCall(e.ServeNumber == 2 ? "ДВОЙНАЯ ОШИБКА" : "ОШИБКА", Poor);
                    break;
                case RallyEventType.Let:
                    ShowCall("СЕТКА, ПЕРЕПОДАЧА", TextColour);
                    break;
                case RallyEventType.PointEnded:
                    AnnouncePoint(e);
                    break;
            }
        }

        void AnnouncePoint(in RallyEvent e)
        {
            TennisScore score = game.Session.Score;
            string winner = game.PlayerName(e.Slot);
            switch (e.ScoreChange)
            {
                case ScoreChange.Match: ShowCall($"МАТЧ · {winner}", Accent); return;
                case ScoreChange.Set: ShowCall($"СЕТ · {winner}", Accent); return;
                case ScoreChange.Game: ShowCall($"ГЕЙМ · {winner}", Accent); return;
            }
            if (e.Reason == PointEndReason.Ace) ShowCall("ЭЙС!", Accent);
            else if (score.IsDeuce) ShowCall("РОВНО", TextColour);
            else if (score.Advantage.HasValue) ShowCall($"БОЛЬШЕ · {game.PlayerName(score.Advantage.Value)}", TextColour);
            else if (e.Reason == PointEndReason.Out) ShowCall("АУТ", TextColour);
        }

        void ShowCall(string text, Color colour)
        {
            call.text = text;
            call.color = colour;
            callUntil = Time.unscaledTime + CallSeconds;
        }

        void ShowFeedback(in ShotResult shot)
        {
            string quality = shot.Quality == ContactQuality.Perfect ? "PERFECT" : shot.Quality == ContactQuality.Good ? "GOOD" : "POOR";
            string timing = Mathf.Abs(shot.TimingError) < 0.03f ? "" : shot.TimingError < 0f ? " · рано" : " · поздно";
            feedback.text = $"{quality}{timing}   {KindName(shot.Kind)} · {shot.Velocity.magnitude * KmhPerMs:F0} км/ч";
            feedback.color = shot.Quality == ContactQuality.Perfect ? Perfect : shot.Quality == ContactQuality.Good ? Good : Poor;
            feedbackUntil = Time.unscaledTime + FeedbackSeconds;
        }

        void FillResult()
        {
            TennisScore score = game.Session.Score;
            PlayerSlot winner = score.MatchWinner ?? PlayerSlot.Near;
            bool humanWon = game.IsHuman(winner);
            bool bothHuman = game.IsHuman(PlayerSlot.Near) && game.IsHuman(PlayerSlot.Far);
            bool noHuman = !game.IsHuman(PlayerSlot.Near) && !game.IsHuman(PlayerSlot.Far);
            resultTitle.text = bothHuman ? $"ПОБЕДА · {game.PlayerName(winner)}" : noHuman ? "МАТЧ ОКОНЧЕН" : humanWon ? "ПОБЕДА!" : "ПОРАЖЕНИЕ";
            resultTitle.color = humanWon || bothHuman ? Accent : TextColour;
            resultScore.text = string.Join("   ", score.CompletedSets.Select(s => $"{s.near}:{s.far}"));
        }

        static void Set(Text text, string value)
        {
            if (text.text != value) text.text = value;
        }

        static string KindName(ShotKind kind)
        {
            switch (kind)
            {
                case ShotKind.Topspin: return "топспин";
                case ShotKind.Slice: return "резаный";
                case ShotKind.Flat: return "плоский";
                case ShotKind.Lob: return "свеча";
                case ShotKind.Drop: return "укороченный";
                case ShotKind.Volley: return "с лёта";
                case ShotKind.Defensive: return "защитный";
                case ShotKind.HalfVolley: return "с полулёта";
                case ShotKind.ServeFlat: return "плоская подача";
                case ShotKind.ServeSlice: return "резаная подача";
                case ShotKind.ServeKick: return "кик";
                default: return "смэш";
            }
        }

        // ---------------------------------------------------------------- Actions

        void Play()
        {
            int opponent = opponentRow.index;
            game.StartMatch(new MatchSetup
            {
                Rules = LengthRules[lengthRow.index],
                Level = (AiLevel)Mathf.Min(opponent, 2),
                VersusHuman = opponent == 3,
                Evening = timeRow.index == 1,
            });
        }

        // ---------------------------------------------------------------- Building

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var system = new GameObject("EventSystem");
            system.AddComponent<EventSystem>();
            system.AddComponent<InputSystemUIInputModule>();
        }

        void BuildCanvas()
        {
            var canvasObject = new GameObject("HUD Canvas");
            canvasObject.transform.SetParent(transform, false);
            canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasObject.AddComponent<GraphicRaycaster>();
            canvasRoot = (RectTransform)canvasObject.transform;
        }

        void BuildHud()
        {
            hud = Group("HUD", canvasRoot);

            RectTransform panel = Box("Score", hud.transform, new Vector2(0f, 1f), new Vector2(24f, -24f), new Vector2(500f, 112f), PanelColour);
            Label(panel, "СЕТЫ", new Vector2(300f, -2f), new Vector2(60f, 20f), 15, Muted, TextAnchor.MiddleCenter);
            Label(panel, "ГЕЙМЫ", new Vector2(370f, -2f), new Vector2(70f, 20f), 15, Muted, TextAnchor.MiddleCenter);
            for (int i = 0; i < 2; i++)
            {
                float y = -22f - i * 44f;
                serving[i] = Label(panel, "", new Vector2(12f, y), new Vector2(24f, 44f), 22, Accent, TextAnchor.MiddleCenter);
                names[i] = Label(panel, "", new Vector2(40f, y), new Vector2(250f, 44f), 30, TextColour, TextAnchor.MiddleLeft, true);
                sets[i] = Label(panel, "", new Vector2(300f, y), new Vector2(60f, 44f), 30, TextColour, TextAnchor.MiddleCenter, true);
                games[i] = Label(panel, "", new Vector2(370f, y), new Vector2(70f, 44f), 30, TextColour, TextAnchor.MiddleCenter, true);
                points[i] = Label(panel, "", new Vector2(430f, y), new Vector2(70f, 44f), 32, Accent, TextAnchor.MiddleCenter, true);
            }
            tieBreak = Label(hud.transform, "", Anchored(new Vector2(0f, 1f), new Vector2(24f, -140f)), new Vector2(500f, 30f), 22, Accent, TextAnchor.MiddleLeft, true);

            call = Label(hud.transform, "", Anchored(new Vector2(0.5f, 0.72f), Vector2.zero), new Vector2(1600f, 100f), 72, Accent, TextAnchor.MiddleCenter, true);
            call.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);
            feedback = Label(hud.transform, "", Anchored(new Vector2(0.5f, 0.14f), Vector2.zero), new Vector2(1400f, 60f), 36, Good, TextAnchor.MiddleCenter, true);
            feedback.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);
            hint = Label(hud.transform, "", Anchored(new Vector2(0.5f, 0.06f), Vector2.zero), new Vector2(1800f, 44f), 24, TextColour, TextAnchor.MiddleCenter);
            hint.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.9f);

            for (int i = 0; i < 2; i++)
            {
                // Placed by screen position, so anchored at the bottom left of the screen.
                powerBars[i] = Box("Power " + i, hud.transform, Vector2.zero, Vector2.zero, new Vector2(PowerBarWidth, PowerBarHeight), PanelColour, centred: true);
                var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
                fill.transform.SetParent(powerBars[i], false);
                var rect = (RectTransform)fill.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.anchoredPosition = new Vector2(PowerBarInset, 0f);
                powerFills[i] = rect;
                powerBars[i].gameObject.SetActive(false);
            }
        }

        void BuildMenu()
        {
            menu = Group("Menu", canvasRoot);
            RectTransform panel = Box("Menu Panel", menu.transform, new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), new Vector2(820f, 760f), PanelColour, centred: true);

            Label(panel, "TENNIS SIMULATOR", new Vector2(0f, -36f), new Vector2(820f, 80f), 64, Accent, TextAnchor.MiddleCenter, true, centred: true);
            Label(panel, "Выставочный корт · хард", new Vector2(0f, -116f), new Vector2(820f, 36f), 26, Muted, TextAnchor.MiddleCenter, centred: true);

            opponentRow = Option(panel, "Соперник", OpponentChoices, 1, new Vector2(0f, -190f));
            lengthRow = Option(panel, "Матч", LengthChoices, 1, new Vector2(0f, -270f));
            timeRow = Option(panel, "Время", TimeChoices, 0, new Vector2(0f, -350f));
            playButton = MenuButton(panel, "ИГРАТЬ", new Vector2(0f, -450f), Play);
            Selectable settingsButton = null;
            settingsButton = MenuButton(panel, "НАСТРОЙКИ", new Vector2(0f, -530f), () => OpenSettings(menu, settingsButton.gameObject));
            Selectable quit = CanQuit ? MenuButton(panel, "ВЫХОД", new Vector2(0f, -610f), () => game.Quit()) : null;
            Chain(opponentRow, lengthRow, timeRow, playButton, settingsButton, quit);

            Label(menu.transform, "Стик / WASD — бег     A / Space — удар: держите, пока копится сила, и ведите прицел WASD; отпустите — удар     B, X, Y, LB, RB / U, H, I, O, L — другие удары     Start / Esc — пауза",
                Anchored(new Vector2(0.5f, 0.08f), Vector2.zero), new Vector2(1880f, 40f), 22, TextColour, TextAnchor.MiddleCenter)
                .gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.9f);
        }

        void BuildPause()
        {
            pause = Group("Pause", canvasRoot);
            Image dim = pause.AddComponent<Image>();
            dim.color = new Color(0f, 0f, 0f, 0.45f);
            Stretch((RectTransform)pause.transform);
            RectTransform panel = Box("Pause Panel", pause.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560f, 480f), PanelColour, centred: true);
            Label(panel, "ПАУЗА", new Vector2(0f, -40f), new Vector2(560f, 70f), 56, Accent, TextAnchor.MiddleCenter, true, centred: true);
            resumeButton = MenuButton(panel, "ПРОДОЛЖИТЬ", new Vector2(0f, -140f), () => game.Pause(false));
            Selectable settingsButton = null;
            settingsButton = MenuButton(panel, "НАСТРОЙКИ", new Vector2(0f, -220f), () => OpenSettings(pause, settingsButton.gameObject));
            Selectable toMenu = MenuButton(panel, "В МЕНЮ", new Vector2(0f, -300f), () => game.ShowMenu());
            Selectable quit = CanQuit ? MenuButton(panel, "ВЫХОД", new Vector2(0f, -380f), () => game.Quit()) : null;
            Chain(resumeButton, settingsButton, toMenu, quit);
        }

        void BuildResult()
        {
            result = Group("Result", canvasRoot);
            RectTransform panel = Box("Result Panel", result.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(700f, 420f), PanelColour, centred: true);
            resultTitle = Label(panel, "", new Vector2(0f, -50f), new Vector2(700f, 80f), 64, Accent, TextAnchor.MiddleCenter, true, centred: true);
            resultScore = Label(panel, "", new Vector2(0f, -140f), new Vector2(700f, 60f), 44, TextColour, TextAnchor.MiddleCenter, true, centred: true);
            rematchButton = MenuButton(panel, "РЕВАНШ", new Vector2(0f, -240f), () => game.StartMatch(game.Setup));
            Selectable toMenu = MenuButton(panel, "В МЕНЮ", new Vector2(0f, -320f), () => game.ShowMenu());
            Chain(rematchButton, toMenu);
        }

        // ---------------------------------------------------------------- Widgets

        static GameObject Group(string name, Transform parent)
        {
            var group = new GameObject(name, typeof(RectTransform));
            group.transform.SetParent(parent, false);
            Stretch((RectTransform)group.transform);
            return group;
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        /// <summary>An anchor and an offset from it, for labels placed directly on the screen.</summary>
        static (Vector2 anchor, Vector2 offset) Anchored(Vector2 anchor, Vector2 offset) => (anchor, offset);

        static RectTransform Box(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size, Color colour, bool centred = false)
        {
            var box = new GameObject(name, typeof(RectTransform), typeof(Image));
            box.transform.SetParent(parent, false);
            var rect = (RectTransform)box.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = centred ? new Vector2(0.5f, 0.5f) : new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            box.GetComponent<Image>().color = colour;
            return rect;
        }

        /// <summary>A label inside a panel, placed from the panel's top left, or its top centre when centred.</summary>
        Text Label(RectTransform panel, string text, Vector2 position, Vector2 size, int fontSize, Color colour, TextAnchor alignment, bool bold = false, bool centred = false)
        {
            Text label = NewText(panel, text, fontSize, colour, alignment, bold);
            var rect = label.rectTransform;
            rect.anchorMin = rect.anchorMax = centred ? new Vector2(0.5f, 1f) : new Vector2(0f, 1f);
            rect.pivot = centred ? new Vector2(0.5f, 1f) : new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return label;
        }

        Text Label(Transform parent, string text, (Vector2 anchor, Vector2 offset) place, Vector2 size, int fontSize, Color colour, TextAnchor alignment, bool bold = false)
        {
            Text label = NewText(parent, text, fontSize, colour, alignment, bold);
            var rect = label.rectTransform;
            rect.anchorMin = rect.anchorMax = place.anchor;
            rect.pivot = alignment == TextAnchor.MiddleLeft ? new Vector2(0f, 0.5f) : new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = place.offset;
            rect.sizeDelta = size;
            return label;
        }

        Text NewText(Transform parent, string text, int fontSize, Color colour, TextAnchor alignment, bool bold)
        {
            var textObject = new GameObject("Text", typeof(RectTransform));
            textObject.transform.SetParent(parent, false);
            var label = textObject.AddComponent<Text>();
            label.font = font;
            label.fontSize = fontSize;
            label.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            label.color = colour;
            label.alignment = alignment;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            label.text = text;
            return label;
        }

        static ColorBlock Colours() => new ColorBlock
        {
            normalColor = ButtonNormal,
            highlightedColor = ButtonSelected,
            selectedColor = ButtonSelected,
            pressedColor = ButtonSelected * 1.2f,
            disabledColor = ButtonNormal * 0.6f,
            colorMultiplier = 1f,
            fadeDuration = 0.08f,
        };

        Selectable MenuButton(RectTransform panel, string caption, Vector2 position, UnityEngine.Events.UnityAction onClick)
        {
            RectTransform rect = Box(caption, panel, new Vector2(0.5f, 1f), position, new Vector2(520f, 64f), Color.white);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = position;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = rect.GetComponent<Image>();
            button.colors = Colours();
            button.onClick.AddListener(onClick);
            Text label = NewText(rect, caption, 32, TextColour, TextAnchor.MiddleCenter, true);
            Stretch(label.rectTransform);
            return button;
        }

        OptionRow Option(RectTransform panel, string caption, string[] choices, int initial, Vector2 position)
        {
            RectTransform rect = Box(caption, panel, new Vector2(0.5f, 1f), position, new Vector2(700f, 64f), Color.white);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = position;
            var row = rect.gameObject.AddComponent<OptionRow>();
            row.targetGraphic = rect.GetComponent<Image>();
            row.colors = Colours();
            row.label = NewText(rect, caption, 28, Muted, TextAnchor.MiddleLeft, false);
            row.label.rectTransform.anchorMin = new Vector2(0f, 0f);
            row.label.rectTransform.anchorMax = new Vector2(0.35f, 1f);
            row.label.rectTransform.offsetMin = new Vector2(24f, 0f);
            row.label.rectTransform.offsetMax = Vector2.zero;
            row.value = NewText(rect, "", 30, TextColour, TextAnchor.MiddleCenter, true);
            row.value.rectTransform.anchorMin = new Vector2(0.3f, 0f);
            row.value.rectTransform.anchorMax = new Vector2(1f, 1f);
            row.value.rectTransform.offsetMin = row.value.rectTransform.offsetMax = Vector2.zero;
            row.choices = choices;
            row.index = initial;
            row.Show();
            return row;
        }

        /// <summary>A page in a browser cannot close itself, so the web build has no quit button.</summary>
        static bool CanQuit => Application.platform != RuntimePlatform.WebGLPlayer;

        /// <summary>Up and down move through these in order, and only through these. Missing ones are skipped.</summary>
        static void Chain(params Selectable[] items)
        {
            items = items.Where(item => item != null).ToArray();
            for (int i = 0; i < items.Length; i++)
            {
                items[i].navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnUp = items[(i - 1 + items.Length) % items.Length],
                    selectOnDown = items[(i + 1) % items.Length],
                };
            }
        }
    }
}
