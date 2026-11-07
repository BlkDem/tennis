using System;
using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    /// <summary>
    /// The one channel through which game systems learn what happened on court.
    /// Audio, HUD, crowd and match scoring subscribe here and never touch each other directly.
    /// </summary>
    public static class GameEvents
    {
        public static event Action<RallyEvent> RallyEvent;

        public static void Raise(in RallyEvent e) => RallyEvent?.Invoke(e);

        /// <summary>Subscribers must not survive into the next play session when domain reload is disabled.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() => RallyEvent = null;
    }
}
