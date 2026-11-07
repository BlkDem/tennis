using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    /// <summary>The stadium's scoreboard: sets, games and points for both players, who serves, and the last serve's speed.</summary>
    public sealed class Scoreboard : MonoBehaviour
    {
        const float KmhPerMs = 3.6f;

        public GameManager game;
        public TextMesh[] names = new TextMesh[2];
        public TextMesh[] sets = new TextMesh[2];
        public TextMesh[] games = new TextMesh[2];
        public TextMesh[] points = new TextMesh[2];
        public TextMesh[] serving = new TextMesh[2];
        public TextMesh info;

        string serveSpeed = "";

        void OnEnable() => GameEvents.RallyEvent += OnRallyEvent;
        void OnDisable() => GameEvents.RallyEvent -= OnRallyEvent;

        void OnRallyEvent(RallyEvent e)
        {
            if (e.Type == RallyEventType.Hit && game != null && game.Session.RallyHits == 1)
                serveSpeed = $"ПОДАЧА {e.Shot.Velocity.magnitude * KmhPerMs:F0} КМ/Ч";
        }

        void LateUpdate()
        {
            if (game == null || game.Session == null) return;
            RallySession session = game.Session;
            TennisScore score = session.Score;

            for (int i = 0; i < 2; i++)
            {
                var slot = (PlayerSlot)i;
                Set(names[i], game.PlayerName(slot));
                Set(sets[i], score.Sets(slot).ToString());
                Set(games[i], score.Games(slot).ToString());
                Set(points[i], PointText(score, slot));
                Set(serving[i], session.Server == slot ? "●" : "");
            }
            Set(info, serveSpeed);
        }

        static string PointText(TennisScore score, PlayerSlot slot)
        {
            if (score.IsTieBreak) return score.PointText(slot);
            if (score.IsDeuce) return "40";
            PlayerSlot? advantage = score.Advantage;
            if (advantage.HasValue) return advantage.Value == slot ? "AD" : "";
            return TennisScore.PointCall(score.GamePoints(slot));
        }

        static void Set(TextMesh mesh, string text)
        {
            if (mesh != null && mesh.text != text) mesh.text = text;
        }
    }
}
