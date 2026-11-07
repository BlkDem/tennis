using System.Text;
using TennisSim.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TennisSim
{
    /// <summary>Test readout for tuning the mechanics: last shot, last point, device status and controls. Not the game's HUD.</summary>
    public sealed class PrototypeOverlay : MonoBehaviour
    {
        const float KmhPerMs = 3.6f;
        const float RpmPerRadS = 60f / (2f * Mathf.PI);

        public GameManager game;

        string lastShot = "—";
        string lastPoint = "—";
        bool showHelp = true;
        GUIStyle style;

        readonly StringBuilder text = new StringBuilder(512);

        void OnEnable() => GameEvents.RallyEvent += OnRallyEvent;
        void OnDisable() => GameEvents.RallyEvent -= OnRallyEvent;

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame) showHelp = !showHelp;
        }

        void OnRallyEvent(RallyEvent e)
        {
            if (e.Type == RallyEventType.Hit)
            {
                ShotResult s = e.Shot;
                lastShot = $"{SlotName(e.Slot)}: {KindName(s.Kind)}, {(s.Side == HandSide.Forehand ? "справа" : "слева")} — {QualityName(s.Quality)} ({s.Score:F2}), "
                         + $"{s.Velocity.magnitude * KmhPerMs:F0} км/ч, {s.Spin.magnitude * RpmPerRadS:F0} об/мин";
            }
            else if (e.Type == RallyEventType.PointEnded)
            {
                lastPoint = $"очко: {SlotName(e.Slot)} ({ReasonName(e.Reason)})";
            }
        }

        void OnGUI()
        {
            if (game == null || game.Session == null) return;

            style ??= new GUIStyle(GUI.skin.label) { fontSize = 16, richText = true, wordWrap = false };

            RallySession session = game.Session;
            text.Clear();
            text.Append("Счёт очков (проверка механик): ").Append(session.Points(PlayerSlot.Near)).Append(" : ").Append(session.Points(PlayerSlot.Far));
            text.Append("   ударов в розыгрыше: ").Append(session.RallyHits).Append('\n');
            text.Append(lastShot).Append('\n').Append(lastPoint).Append('\n');

            if (session.State == SessionState.AwaitingServe)
                text.Append("<b>Подача: ").Append(SlotName(session.Server)).Append(" — нажмите кнопку удара, мяч упадёт; ударьте его после отскока</b>\n");

            text.Append("Геймпадов: ").Append(game.inputManager.GamepadCount);
            text.Append("   дальний игрок: ").Append(game.FarIsBot ? "бот (F2)" : "человек (F2)");
            text.Append("   автопозиция: ").Append(game.AutoPositioning ? "вкл (F3)" : "выкл (F3)").Append('\n');

            if (showHelp)
            {
                text.Append("\nНажать — подготовка, отпустить — замах. Точный момент = Perfect; удержание через мяч = Good.\n");
                text.Append("Клавиатура: WASD ход/прицел, Shift бег, Space авто, J справа, K слева, U резаный, H плоский, L с лёта, I свеча, O укороченный\n");
                text.Append("Геймпад: левый стик ход/прицел, правый стик прицел, RT бег, A авто, B резаный, X плоский, Y свеча, RB с лёта, LB укороченный, крестовина ←/→ слева/справа\n");
                text.Append("Игрок 2: стрелки, правый Shift, Num0 авто, Num1 справа, Num2 слева, Num3 с лёта, Num4 резаный, Num5 плоский, Num6 свеча, Num7 укороченный\n");
                text.Append("F1 скрыть подсказку");
            }

            var area = new Rect(16f, 12f, Screen.width - 32f, Screen.height - 24f);
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), text.ToString(), style);
            GUI.color = Color.white;
            GUI.Label(area, text.ToString(), style);
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
                default: return "смэш";
            }
        }

        static string QualityName(ContactQuality quality) => quality.ToString();

        static string ReasonName(PointEndReason reason)
        {
            switch (reason)
            {
                case PointEndReason.Out: return "аут";
                case PointEndReason.Net: return "сетка";
                case PointEndReason.DoubleBounce: return "два отскока";
                case PointEndReason.OwnSide: return "мяч на своей половине";
                default: return "—";
            }
        }
    }
}
