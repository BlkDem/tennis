using System;
using TennisSim.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TennisSim
{
    public enum GamePhase { Menu, Playing, Paused, MatchOver }

    /// <summary>What the player chose in the menu.</summary>
    [Serializable]
    public struct MatchSetup
    {
        public MatchRules Rules;
        public AiLevel Level;
        /// <summary>The far player is a second human instead of the AI.</summary>
        public bool VersusHuman;
        /// <summary>The near player is the AI too: the menu's background match, demos and tests.</summary>
        public bool NearIsAi;
        /// <summary>An evening match under the floodlights.</summary>
        public bool Evening;

        public static MatchSetup Default => new MatchSetup { Rules = MatchRules.OneSet, Level = AiLevel.Medium };
    }

    /// <summary>
    /// Runs the gameplay at a fixed tick and connects it to the scene: intents go in, events and state come out.
    /// The rally logic lives in <see cref="RallySession"/>, so this class stays a thin host. It also owns the
    /// game's flow: a menu with an AI match playing behind it, the match, the pause and the result.
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
        public EnvironmentController environment;

        [Tooltip("Open on the menu. Off goes straight into a match against the AI, as the prototype did.")]
        public bool startInMenu = true;

        /// <summary>How long the players warm up before the menu's exhibition match, seconds: once through the warm-up.</summary>
        const float WarmUpSeconds = 18f;
        float warmUpUntil;

        System.Random rng;
        int seed;
        OpponentAI farAi;
        bool farIsBot;

        public RallySession Session { get; private set; }
        public GamePhase Phase { get; private set; }
        public MatchSetup Setup { get; private set; }
        public bool FarIsBot => farIsBot;
        /// <summary>Behind the menu, the players warm up before their exhibition match starts; play waits meanwhile.</summary>
        public bool WarmingUp => Phase == GamePhase.Menu && Time.time < warmUpUntil;
        public AiLevel AiLevel => Setup.Level;
        public bool AutoPositioning { get; private set; } = true;

        /// <summary>Raised whenever a new session replaces the old one, or the phase changes.</summary>
        public event Action Changed;

        public bool IsHuman(PlayerSlot slot) => slot == PlayerSlot.Near ? !Setup.NearIsAi : !farIsBot;

        public string PlayerName(PlayerSlot slot)
        {
            if (!IsHuman(slot)) return "ИИ";
            return slot == PlayerSlot.Near ? "ИГРОК 1" : "ИГРОК 2";
        }

        void Awake()
        {
            Time.fixedDeltaTime = RallySession.TickDt;
            GameSettings.ApplyGraphics();
            seed = randomSeed != 0 ? randomSeed : Environment.TickCount;
            rng = new System.Random(seed ^ 0x5bd1e995);
            inputManager.DevicesChanged += OnDevicesChanged;

            if (startInMenu) ShowMenu();
            else StartMatch(new MatchSetup { Rules = MatchRules.BestOfThree, Level = AiLevel.Medium, VersusHuman = inputManager.GamepadCount >= 2 });
        }

        void OnDestroy()
        {
            if (inputManager != null) inputManager.DevicesChanged -= OnDevicesChanged;
            Time.timeScale = 1f;
        }

        void FixedUpdate()
        {
            ShowWarmUp();
            if (WarmingUp) return;
            Session.Tick(RallySession.TickDt);

            foreach (RallyEvent e in Session.Events)
                GameEvents.Raise(e);

            ball.OnSessionTicked();
            nearPlayer.OnSessionTicked();
            farPlayer.OnSessionTicked();

            if (Phase == GamePhase.Playing && Session.State == SessionState.MatchOver) SetPhase(GamePhase.MatchOver);
            // Behind the menu the AI match just carries on.
            if (Phase == GamePhase.Menu && Session.State == SessionState.MatchOver) ShowMenu();
        }

        void ShowWarmUp()
        {
            bool warming = WarmingUp;
            ball.Hidden = warming;
            foreach (PlayerController player in new[] { nearPlayer, farPlayer })
            {
                var animation = player.GetComponent<PlayerAnimationController>();
                if (animation != null) animation.WarmingUp = warming;
            }
        }

        void Update()
        {
            ShowWarmUp();
            Keyboard keyboard = Keyboard.current;
            Gamepad gamepad = Gamepad.current;
            bool pausePressed = (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) || (gamepad != null && gamepad.startButton.wasPressedThisFrame);
            if (pausePressed && !PauseKeysBlocked)
            {
                if (Phase == GamePhase.Playing) Pause(true);
                else if (Phase == GamePhase.Paused) Pause(false);
            }

            if (keyboard == null || Phase != GamePhase.Playing) return;
            if (keyboard.f2Key.wasPressedThisFrame) ApplyFarPlayerControl(!farIsBot);
            if (keyboard.f3Key.wasPressedThisFrame) SetAutoPositioning(!AutoPositioning);
            if (keyboard.f4Key.wasPressedThisFrame) SetAiLevel((AiLevel)(((int)AiLevel + 1) % 3));
        }

        // ---------------------------------------------------------------- Flow

        public void StartMatch(MatchSetup setup)
        {
            Setup = setup;
            NewSession(setup.Rules);
            inputManager.Near.Clear();
            inputManager.Far.Clear();

            PlayerAgent near = Session.Agent(PlayerSlot.Near);
            near.AutoPositioning = setup.NearIsAi || AutoPositioning;
            near.Assisted = !setup.NearIsAi;
            near.IntentSource = setup.NearIsAi ? new OpponentAI(near, setup.Level, rng) : (IPlayerIntentSource)inputManager.Near;
            farAi = null;
            ApplyFarPlayerControl(!setup.VersusHuman);
            if (environment != null) environment.Apply(setup.Evening);

            Time.timeScale = 1f;
            warmUpUntil = 0f;
            SetPhase(GamePhase.Playing);
        }

        /// <summary>The menu, over an AI match that keeps the stadium alive.</summary>
        public void ShowMenu()
        {
            Setup = new MatchSetup { Rules = MatchRules.OneSet, Level = AiLevel.Hard, NearIsAi = true };
            NewSession(Setup.Rules);
            PlayerAgent near = Session.Agent(PlayerSlot.Near);
            near.AutoPositioning = true;
            near.Assisted = false;
            near.IntentSource = new OpponentAI(near, AiLevel.Hard, rng);
            farAi = null;
            ApplyFarPlayerControl(true);
            if (environment != null) environment.Apply(false);
            Time.timeScale = 1f;
            warmUpUntil = Time.time + WarmUpSeconds;
            SetPhase(GamePhase.Menu);
        }

        /// <summary>Set by the settings screen, which takes Esc and Start for itself while it is open.</summary>
        public bool PauseKeysBlocked { get; set; }

        public void Pause(bool paused)
        {
            if (paused == (Phase == GamePhase.Paused)) return;
            Time.timeScale = paused ? 0f : 1f;
            // Buttons pressed in the pause menu are not shots.
            if (!paused)
            {
                inputManager.Near.Clear();
                inputManager.Far.Clear();
            }
            SetPhase(paused ? GamePhase.Paused : GamePhase.Playing);
        }

        public void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        void NewSession(MatchRules rules)
        {
            Session = new RallySession(ballConfig.Params, shotConfig.Tuning, playerConfig.Movement, seed++, rules);
            ball.Bind(Session);
            nearPlayer.Bind(Session, PlayerSlot.Near);
            farPlayer.Bind(Session, PlayerSlot.Far);
        }

        void SetPhase(GamePhase phase)
        {
            Phase = phase;
            Changed?.Invoke();
        }

        // ---------------------------------------------------------------- Controls during a match

        public void SetAiLevel(AiLevel level)
        {
            MatchSetup setup = Setup;
            setup.Level = level;
            Setup = setup;
            farAi?.SetLevel(level);
        }

        void OnDevicesChanged()
        {
            if (Phase == GamePhase.Playing && inputManager.GamepadCount >= 2 && farIsBot) ApplyFarPlayerControl(false);
        }

        void ApplyFarPlayerControl(bool useBot)
        {
            farIsBot = useBot;
            PlayerAgent far = Session.Agent(PlayerSlot.Far);
            farAi ??= new OpponentAI(far, Setup.Level, rng);
            far.AutoPositioning = useBot || AutoPositioning;
            far.Assisted = !useBot;
            far.IntentSource = useBot ? farAi : (IPlayerIntentSource)inputManager.Far;
        }

        void SetAutoPositioning(bool value)
        {
            AutoPositioning = value;
            if (!Setup.NearIsAi) Session.Agent(PlayerSlot.Near).AutoPositioning = value;
            if (!farIsBot) Session.Agent(PlayerSlot.Far).AutoPositioning = value;
        }
    }
}
