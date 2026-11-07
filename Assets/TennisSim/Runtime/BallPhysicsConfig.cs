using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    [CreateAssetMenu(menuName = "Tennis/Ball Physics Config", fileName = "BallPhysicsConfig")]
    public sealed class BallPhysicsConfig : ScriptableObject
    {
        public BallPhysicsParams Params = BallPhysicsParams.Default;
    }
}
