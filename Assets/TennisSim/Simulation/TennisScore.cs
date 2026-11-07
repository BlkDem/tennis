namespace TennisSim.Simulation
{
    public enum ScoreChange { Point, Game, Set }

    /// <summary>
    /// Tennis scoring: points, games with deuce and advantage, sets won by two games.
    /// It decides who serves and from which side. There is no tie-break yet: a set at 6:6 plays on.
    /// </summary>
    public sealed class TennisScore
    {
        const int GamesForSet = 6;

        readonly int[] points = new int[2];
        readonly int[] games = new int[2];
        readonly int[] sets = new int[2];

        public PlayerSlot Server { get; private set; }

        public TennisScore(PlayerSlot firstServer) => Server = firstServer;

        public int GamePoints(PlayerSlot slot) => points[(int)slot];
        public int Games(PlayerSlot slot) => games[(int)slot];
        public int Sets(PlayerSlot slot) => sets[(int)slot];

        /// <summary>Even points into a game are served from the right, the deuce side.</summary>
        public bool DeuceSide => (points[0] + points[1]) % 2 == 0;

        public bool IsDeuce => points[0] >= 3 && points[0] == points[1];

        /// <summary>The player one point from the game after deuce, if any.</summary>
        public PlayerSlot? Advantage
        {
            get
            {
                if (points[0] < 3 || points[1] < 3 || points[0] == points[1]) return null;
                return points[0] > points[1] ? PlayerSlot.Near : PlayerSlot.Far;
            }
        }

        public ScoreChange AddPoint(PlayerSlot winner)
        {
            int w = (int)winner;
            int l = 1 - w;
            points[w]++;
            if (points[w] < 4 || points[w] - points[l] < 2) return ScoreChange.Point;

            points[0] = points[1] = 0;
            games[w]++;
            Server = CourtGeometry.Opponent(Server);
            if (games[w] < GamesForSet || games[w] - games[l] < 2) return ScoreChange.Game;

            games[0] = games[1] = 0;
            sets[w]++;
            return ScoreChange.Set;
        }

        /// <summary>The traditional call for one player's points: 0, 15, 30, 40.</summary>
        public static string PointCall(int points) => points switch { 0 => "0", 1 => "15", 2 => "30", _ => "40" };
    }
}
