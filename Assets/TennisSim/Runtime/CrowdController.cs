using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    /// <summary>
    /// The crowd's mood, read by the crowd shader: quiet through the serve and the rally, on its feet after a
    /// point, more so after a long rally or an ace. The shader turns this one number into poses per spectator.
    /// </summary>
    public sealed class CrowdController : MonoBehaviour
    {
        static readonly int Excitement = Shader.PropertyToID("_CrowdExcitement");

        [Tooltip("Excitement after an ordinary point, 0..1.")]
        public float pointExcitement = 0.5f;
        [Tooltip("Added per shot of the rally, up to Long Rally Shots.")]
        public float perShot = 0.04f;
        public int longRallyShots = 12;
        [Tooltip("How long the applause holds before it dies down, seconds.")]
        public float applauseHold = 1.6f;
        [Tooltip("How fast it dies down, per second.")]
        public float settleRate = 0.4f;

        float level;
        float holdUntil;
        int shots;

        public float Level => level;

        void OnEnable()
        {
            GameEvents.RallyEvent += OnRallyEvent;
            Shader.SetGlobalFloat(Excitement, 0f);
        }

        void OnDisable()
        {
            GameEvents.RallyEvent -= OnRallyEvent;
            Shader.SetGlobalFloat(Excitement, 0f);
        }

        void OnRallyEvent(RallyEvent e)
        {
            switch (e.Type)
            {
                case RallyEventType.ServeToss:
                    shots = 0;
                    break;
                case RallyEventType.Hit:
                    shots++;
                    break;
                case RallyEventType.PointEnded:
                    float rally = Mathf.Min(shots, longRallyShots) * perShot;
                    float cheer = e.Reason == PointEndReason.Ace ? 1f : e.Reason == PointEndReason.DoubleFault ? 0.2f : pointExcitement + rally;
                    if (e.ScoreChange != ScoreChange.Point) cheer = Mathf.Max(cheer, 0.85f);
                    level = Mathf.Max(level, Mathf.Clamp01(cheer));
                    holdUntil = Time.time + applauseHold;
                    break;
            }
        }

        void Update()
        {
            if (Time.time >= holdUntil) level = Mathf.MoveTowards(level, 0f, settleRate * Time.deltaTime);
            Shader.SetGlobalFloat(Excitement, level);
        }
    }
}
