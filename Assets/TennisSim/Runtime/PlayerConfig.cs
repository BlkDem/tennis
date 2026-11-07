using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    [CreateAssetMenu(menuName = "Tennis/Player Config", fileName = "PlayerConfig")]
    public sealed class PlayerConfig : ScriptableObject
    {
        public PlayerMovementTuning Movement = new PlayerMovementTuning();
    }
}
