using System.Text;
using TennisSim.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TennisSim
{
    /// <summary>Test readout for tuning the mechanics: score, last shot, serve calls, device status and controls. Not the game's HUD.</summary>
    public sealed class PrototypeOverlay : MonoBehaviour
    {
        const float KmhPerMs = 3.6f;
        const float RpmPerRadS = 60f / (2f * Mathf.PI);
        /// <summary>The layout is designed for this screen height and scaled to the real one.</summary>
        const float ReferenceHeight = 1080f;
        const float CallDisplaySeconds = 1.6f;

        public GameManager game;
        public int fontSize = 22;

        string lastShot = "—";
        string lastPoint = "—";
        string call;
        float callUntil;
        bool showHelp = true;
        GUIStyle style;
        GUIStyle callStyle;

        readonly StringBuilder text = new StringBuilder(1024);

        void OnEnable() => GameEvents.RallyEvent += OnRallyEvent;
        void OnDisable() => GameEvents.RallyEvent -= OnRallyEvent;

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame) showHelp = !showHelp;
        }

        void OnRallyEvent(RallyEvent e)
        {
            switch (e.Type)
            {
                case RallyEventType.Hit:
                    ShotResult s = e.Shot;
                    lastShot = $"{SlotName(e.Slot)}: {KindName(s.Kind)}{SideText(s)} — {s.Quality} ({s.Score:F2}{TimingText(s.TimingError)}), "
                             + $"{s.Velocity.magnitude * KmhPerMs:F0} км/ч, {s.Spin.magnitude * RpmPerRadS:F0} об/мин";
                    break;
                case RallyEventType.Fault:
                    ShowCall(e.ServeNumber == 2 ? "Двойная ошибка" : "Ошибка");
                    break;
                case RallyEventType.Let:
                    ShowCall("Сетка, переподача");
                    break;
                case RallyEventType.PointEnded:
                    lastPoint = $"очко: {SlotName(e.Slot)} ({ReasonName(e.Reason)})";
                    if (e.Reason == PointEndReason.Ace) ShowCall("Эйс!");
                    else if (e.ScoreChange == ScoreChange.Game) ShowCall($"Гейм: {SlotName(e.Slot)}");
                    else if (e.ScoreChange == ScoreChange.Set) ShowCall($"Сет: {SlotName(e.Slot)}");
                    break;
            }
        }

        void ShowCall(string message)
        {
            call = message;
            callUntil = Time.time + CallDisplaySeconds;
        }

        void OnGUI()
        {
            if (game == null || game.Session == null) return;

            float scale = Screen.height / ReferenceHeight;
            style ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = false };
            callStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            style.fontSize = Mathf.RoundToInt(fontSize * scale);
            callStyle.fontSize = Mathf.RoundToInt(fontSize * 2.4f * scale);

            RallySession session = game.Session;
            TennisScore score = session.Score;
            text.Clear();
            text.Append("<b>").Append(ScoreLine(score)).Append("</b>");
            text.Append("   подаёт ").Append(SlotName(session.Server)).Append(session.ServeNumber == 2 ? ", вторая подача" : "");
            text.Append("   ударов в розыгрыше: ").Append(session.RallyHits).Append('\n');
            text.Append(lastShot).Append('\n').Append(lastPoint).Append('\n');

            if (session.State == SessionState.AwaitingServe)
                text.Append("<b>Подача: нажмите — подброс, отпустите — удар, когда мяч опускается. H плоская, U резаная, остальные кнопки — кик</b>\n");

            text.Append("Геймпадов: ").Append(game.inputManager.GamepadCount);
            text.Append("   дальний игрок: ").Append(game.FarIsBot ? "бот (F2)" : "человек (F2)");
            text.Append("   автопозиция: ").Append(game.AutoPositioning ? "вкл (F3)" : "выкл (F3)").Append('\n');

            if (showHelp)
            {
                text.Append("\nНажать — подготовка, отпустить — замах. Точный момент = Perfect; удержание через мяч = Good.\n");
                text.Append("Рано — мяч уходит поперёк корпуса, поздно — в другую сторону. Высокий мяч бьётся сильнее, мяч на вытянутой руке — только защитой.\n");
                text.Append("Перед подачей стик двигает вдоль задней линии, во время подброса — прицел в квадрате.\n");
                text.Append("Клавиатура: WASD ход/прицел, Shift бег, Space авто, J справа, K слева, U резаный, H плоский, L с лёта, I свеча, O укороченный\n");
                text.Append("Геймпад: левый стик ход/прицел, правый стик прицел, RT бег, A авто, B резаный, X плоский, Y свеча, RB с лёта, LB укороченный, крестовина ←/→\n");
                text.Append("Игрок 2: стрелки, правый Shift, Num0 авто, Num1 справа, Num2 слева, Num3 с лёта, Num4 резаный, Num5 плоский, Num6 свеча, Num7 укороченный\n");
                text.Append("F1 скрыть подсказку   Alt+Enter оконный режим");
            }

            float margin = 16f * scale;
            var area = new Rect(margin, margin * 0.75f, Screen.width - margin * 2f, Screen.height - margin * 1.5f);
            DrawShadowed(area, text.ToString(), style);

            if (call != null && Time.time < callUntil)
                DrawShadowed(new Rect(0f, Screen.height * 0.3f, Screen.width, callStyle.fontSize * 1.6f), call, callStyle);
        }

        static void DrawShadowed(Rect area, string content, GUIStyle guiStyle)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(area.x + 2f, area.y + 2f, area.width, area.height), content, guiStyle);
            GUI.color = Color.white;
            GUI.Label(area, content, guiStyle);
        }

        static string ScoreLine(TennisScore score)
        {
            string games = $"сеты {score.Sets(PlayerSlot.Near)}:{score.Sets(PlayerSlot.Far)}   геймы {score.Games(PlayerSlot.Near)}:{score.Games(PlayerSlot.Far)}   ";
            if (score.IsDeuce) return games + "ровно";
            PlayerSlot? advantage = score.Advantage;
            if (advantage.HasValue) return games + "больше: " + SlotName(advantage.Value);
            return games + TennisScore.PointCall(score.GamePoints(PlayerSlot.Near)) + ":" + TennisScore.PointCall(score.GamePoints(PlayerSlot.Far));
        }

        static string SideText(in ShotResult s)
        {
            if (s.Kind == ShotKind.ServeFlat || s.Kind == ShotKind.ServeSlice || s.Kind == ShotKind.ServeKick || s.Kind == ShotKind.Smash) return "";
            return s.Side == HandSide.Forehand ? ", справа" : ", слева";
        }

        static string TimingText(float error)
        {
            if (Mathf.Abs(error) < 0.02f) return "";
            return error < 0f ? $", рано {-error:F2} с" : $", поздно {error:F2} с";
        }

        static string SlotName(PlayerSlot slot) => slot == PlayerSlot.Near ? "Игрок 1" : "Игрок 2";

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

        static string ReasonName(PointEndReason reason)
        {
            switch (reason)
            {
                case PointEndReason.Out: return "аут";
                case PointEndReason.Net: return "сетка";
                case PointEndReason.DoubleBounce: return "два отскока";
                case PointEndReason.OwnSide: return "мяч на своей половине";
                case PointEndReason.DoubleFault: return "двойная ошибка";
                case PointEndReason.Ace: return "эйс";
                default: return "—";
            }
        }
    }
}
