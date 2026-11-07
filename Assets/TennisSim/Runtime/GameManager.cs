using TennisSim.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TennisSim
{
    /// <summary>
    /// Runs the gameplay at a fixed tick and connects it to the scene: intents go in, events and state come out.
    /// The rally logic lives in <see cref="RallySession"/>, so this class stays a thin host.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class GameManager : MonoBehaviour
    {
        [Header("Balance")]
        public BallPhysicsConfig ballConfig;
        public ShotConfig shotConfig;
        public PlayerConfig playerConfig;
        [Tooltip("0 picks a new random seed every run.")]
        public int randomSeed;

        [Header("Scene")]
        public InputManager inputManager;
        public BallController ball;
        public PlayerController nearPlayer;
        public PlayerController farPlayer;

        System.Random botRng;
        bool farIsBot;

        public RallySession Session { get; private set; }
        public bool FarIsBot => farIsBot;
        public bool AutoPositioning { get; private set; } = true;

        void Awake()
        {
            Time.fixedDeltaTime = RallySession.TickDt;

            int seed = randomSeed != 0 ? randomSeed : System.Environment.TickCount;
            botRng = new System.Random(seed ^ 0x5bd1e995);
            Session = new RallySession(ballConfig.Params, shotConfig.Tuning, playerConfig.Movement, seed);

            ball.Bind(Session);
            nearPlayer.Bind(Session, PlayerSlot.Near);
            farPlayer.Bind(Session, PlayerSlot.Far);

            Session.Agent(PlayerSlot.Near).IntentSource = inputManager.Near;
            ApplyFarPlayerControl(inputManager.GamepadCount < 2);
            inputManager.DevicesChanged += OnDevicesChanged;
        }

        void OnDestroy()
        {
            if (inputManager != null) inputManager.DevicesChanged -= OnDevicesChanged;
        }

        void FixedUpdate()
        {
            Session.Tick(RallySession.TickDt);

            foreach (RallyEvent e in Session.Events)
                GameEvents.Raise(e);

            ball.OnSessionTicked();
            nearPlayer.OnSessionTicked();
            farPlayer.OnSessionTicked();
        }

        void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.f2Key.wasPressedThisFrame) ApplyFarPlayerControl(!farIsBot);
            if (keyboard.f3Key.wasPressedThisFrame) SetAutoPositioning(!AutoPositioning);
        }

        void OnDevicesChanged()
        {
            if (inputManager.GamepadCount >= 2 && farIsBot) ApplyFarPlayerControl(false);
        }

        void ApplyFarPlayerControl(bool useBot)
        {
            farIsBot = useBot;
            Session.Agent(PlayerSlot.Far).IntentSource = useBot ? new PartnerBot(botRng) : (IPlayerIntentSource)inputManager.Far;
        }

        void SetAutoPositioning(bool value)
        {
            AutoPositioning = value;
            Session.Agent(PlayerSlot.Near).AutoPositioning = value;
            if (!farIsBot) Session.Agent(PlayerSlot.Far).AutoPositioning = value;
        }

        /// <summary>Hands the near player to a bot, for the automated play tests and hands-off demos.</summary>
        public void UseBotForNearPlayer() => Session.Agent(PlayerSlot.Near).IntentSource = new PartnerBot(botRng);
    }
}
