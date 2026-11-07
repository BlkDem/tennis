using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    [CreateAssetMenu(menuName = "Tennis/Shot Config", fileName = "ShotConfig")]
    public sealed class ShotConfig : ScriptableObject
    {
        public ShotTuning Tuning = new ShotTuning();
    }
}
