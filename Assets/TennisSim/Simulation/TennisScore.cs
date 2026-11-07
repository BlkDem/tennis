namespace TennisSim.Simulation
{
    public enum ScoreChange { Point, Game, Set, Match }

    /// <summary>How long a match is.</summary>
    public struct MatchRules
    {
        public int SetsToWin;
        public int GamesPerSet;

        public static MatchRules BestOfThree => new MatchRules { SetsToWin = 2, GamesPerSet = 6 };
        public static MatchRules OneSet => new MatchRules { SetsToWin = 1, GamesPerSet = 6 };
        public static MatchRules ShortSet => new MatchRules { SetsToWin = 1, GamesPerSet = 4 };
    }

    /// <summary>
    /// Tennis scoring: points, games with deuce and advantage, sets won by two games or a tie-break, and the match.
    /// It decides who serves and from which side.
    /// </summary>
    public sealed class TennisScore
    {
        const int TieBreakPoints = 7;

        readonly MatchRules rules;
        readonly int[] points = new int[2];
        readonly int[] games = new int[2];
        readonly int[] sets = new int[2];
        readonly System.Collections.Generic.List<(int near, int far)> completedSets = new System.Collections.Generic.List<(int, int)>();
        PlayerSlot gameServer;
        PlayerSlot tieBreakFirstServer;

        public TennisScore(PlayerSlot firstServer) : this(firstServer, MatchRules.BestOfThree) { }

        public TennisScore(PlayerSlot firstServer, MatchRules rules)
        {
            this.rules = rules.SetsToWin > 0 && rules.GamesPerSet > 0 ? rules : MatchRules.BestOfThree;
            gameServer = firstServer;
        }

        public MatchRules Rules => rules;
        public int GamePoints(PlayerSlot slot) => points[(int)slot];
        public int Games(PlayerSlot slot) => games[(int)slot];
        public int Sets(PlayerSlot slot) => sets[(int)slot];
        /// <summary>Games in every finished set, near player first.</summary>
        public System.Collections.Generic.IReadOnlyList<(int near, int far)> CompletedSets => completedSets;

        /// <summary>Games are level at the set's length: the set is decided by a tie-break.</summary>
        public bool IsTieBreak => games[0] == rules.GamesPerSet && games[1] == rules.GamesPerSet;
        public bool MatchOver { get; private set; }
        public PlayerSlot? MatchWinner { get; private set; }

        /// <summary>
        /// In a game the server is fixed. In a tie-break the first point is served by the player due to serve,
        /// then the serve changes after every two points.
        /// </summary>
        public PlayerSlot Server
        {
            get
            {
                if (!IsTieBreak) return gameServer;
                int played = points[0] + points[1];
                bool first = played == 0 || ((played - 1) / 2) % 2 == 1;
                return first ? tieBreakFirstServer : CourtGeometry.Opponent(tieBreakFirstServer);
            }
        }

        /// <summary>Even points into a game or tie-break are served from the right, the deuce side.</summary>
        public bool DeuceSide => (points[0] + points[1]) % 2 == 0;

        public bool IsDeuce => !IsTieBreak && points[0] >= 3 && points[0] == points[1];

        /// <summary>The player one point from the game after deuce, if any.</summary>
        public PlayerSlot? Advantage
        {
            get
            {
                if (IsTieBreak || points[0] < 3 || points[1] < 3 || points[0] == points[1]) return null;
                return points[0] > points[1] ? PlayerSlot.Near : PlayerSlot.Far;
            }
        }

        public ScoreChange AddPoint(PlayerSlot winner)
        {
            if (MatchOver) return ScoreChange.Match;
            int w = (int)winner;
            int l = 1 - w;
            bool tieBreak = IsTieBreak;
            points[w]++;

            int needed = tieBreak ? TieBreakPoints : 4;
            if (points[w] < needed || points[w] - points[l] < 2) return ScoreChange.Point;

            points[0] = points[1] = 0;
            games[w]++;
            // After a tie-break the player who received first in it serves the next set.
            gameServer = tieBreak ? CourtGeometry.Opponent(tieBreakFirstServer) : CourtGeometry.Opponent(gameServer);

            bool setWon = tieBreak || (games[w] >= rules.GamesPerSet && games[w] - games[l] >= 2);
            if (!setWon)
            {
                if (IsTieBreak) tieBreakFirstServer = gameServer;
                return ScoreChange.Game;
            }

            completedSets.Add((games[0], games[1]));
            games[0] = games[1] = 0;
            sets[w]++;
            if (sets[w] < rules.SetsToWin) return ScoreChange.Set;

            MatchOver = true;
            MatchWinner = winner;
            return ScoreChange.Match;
        }

        /// <summary>The traditional call for one player's points: 0, 15, 30, 40; in a tie-break, plain numbers.</summary>
        public string PointText(PlayerSlot slot) => IsTieBreak ? points[(int)slot].ToString() : PointCall(points[(int)slot]);

        public static string PointCall(int points) => points switch { 0 => "0", 1 => "15", 2 => "30", _ => "40" };
    }
}
