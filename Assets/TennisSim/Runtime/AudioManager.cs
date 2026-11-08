using TennisSim.Simulation;
using UnityEngine;

namespace TennisSim
{
    /// <summary>
    /// All of the game's sound, driven by game events: shots sound by how they were struck, bounces by how hard the
    /// ball lands, shoes squeak on sharp turns, and the crowd hushes for play and answers each point.
    /// Clips come from Tools/Audio/make_sounds.py.
    /// </summary>
    public sealed class AudioManager : MonoBehaviour
    {
        const float MinBounceSpeed = 1.5f;
        const float SqueakMinSpeed = 3.5f;
        const float SqueakCooldown = 0.35f;
        const int LongRally = 9;

        public GameManager game;

        [Header("Court")]
        public AudioClip[] hitHard;
        public AudioClip[] hitSoft;
        public AudioClip[] hitFrame;
        public AudioClip[] bounce;
        public AudioClip[] squeak;
        public AudioClip net;
        public AudioClip netCord;

        [Header("Crowd")]
        public AudioClip murmur;
        public AudioClip applause;
        public AudioClip cheer;
        public AudioClip ooh;
        [Range(0f, 1f)] public float murmurBetweenPoints = 0.35f;
        [Range(0f, 1f)] public float murmurInPlay = 0.08f;

        [Header("Mix")]
        [Range(0f, 1f)] public float effectsVolume = 1f;
        [Range(0f, 1f)] public float crowdVolume = 0.8f;
        public int voices = 16;

        AudioSource[] pool;
        int nextVoice;
        AudioSource murmurSource;
        AudioSource crowdSource;
        int hitsThisPoint;
        readonly Vector3[] lastVelocity = new Vector3[2];
        readonly float[] lastSqueak = new float[2];

        void Awake()
        {
            pool = new AudioSource[voices];
            for (int i = 0; i < voices; i++)
            {
                var source = new GameObject("Voice " + i).AddComponent<AudioSource>();
                source.transform.SetParent(transform, false);
                source.playOnAwake = false;
                // Half positional: a far shot is quieter and panned, but still clearly heard from the camera's distance.
                source.spatialBlend = 0.55f;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.minDistance = 6f;
                source.maxDistance = 90f;
                source.dopplerLevel = 0f;
                pool[i] = source;
            }

            murmurSource = gameObject.AddComponent<AudioSource>();
            murmurSource.clip = murmur;
            murmurSource.loop = true;
            murmurSource.spatialBlend = 0f;
            murmurSource.volume = murmurBetweenPoints * crowdVolume;
            if (murmur != null) murmurSource.Play();

            crowdSource = gameObject.AddComponent<AudioSource>();
            crowdSource.spatialBlend = 0f;
            crowdSource.playOnAwake = false;
        }

        void OnEnable() => GameEvents.RallyEvent += OnRallyEvent;
        void OnDisable() => GameEvents.RallyEvent -= OnRallyEvent;

        void OnRallyEvent(RallyEvent e)
        {
            switch (e.Type)
            {
                case RallyEventType.ServeToss:
                    hitsThisPoint = 0;
                    break;
                case RallyEventType.Hit:
                    hitsThisPoint++;
                    PlayHit(e);
                    break;
                case RallyEventType.Bounce:
                    if (e.ImpactSpeed > MinBounceSpeed)
                        PlayAt(Pick(bounce), e.Point, Mathf.Clamp(e.ImpactSpeed / 22f, 0.15f, 0.9f), Random.Range(0.95f, 1.06f));
                    break;
                case RallyEventType.NetFabric:
                    PlayAt(net, e.Point, Mathf.Clamp(e.ImpactSpeed / 20f, 0.3f, 1f), Random.Range(0.95f, 1.05f));
                    break;
                case RallyEventType.NetCord:
                    PlayAt(netCord, e.Point, 0.8f, Random.Range(0.97f, 1.03f));
                    break;
                case RallyEventType.Fault:
                    PlayCrowd(ooh, e.ServeNumber == 2 ? 0.8f : 0.45f);
                    break;
                case RallyEventType.PointEnded:
                    bool big = e.Reason == PointEndReason.Ace || hitsThisPoint >= LongRally || e.ScoreChange != ScoreChange.Point;
                    if (e.Reason == PointEndReason.DoubleFault) break;
                    if (big) PlayCrowd(cheer, 0.9f);
                    else PlayCrowd(applause, Mathf.Clamp01(0.45f + hitsThisPoint * 0.05f));
                    break;
            }
        }

        void PlayHit(in RallyEvent e)
        {
            ShotResult shot = e.Shot;
            AudioClip[] set;
            if (shot.Quality == ContactQuality.Poor && shot.Score < 0.35f) set = hitFrame;
            else if (shot.Kind == ShotKind.Slice || shot.Kind == ShotKind.Drop || shot.Kind == ShotKind.Lob || shot.Kind == ShotKind.Defensive) set = hitSoft;
            else set = hitHard;

            float speed = shot.Velocity.magnitude;
            float volume = Mathf.Clamp(speed / 40f, 0.35f, 1f);
            // A cleanly struck, fast ball rings a little higher.
            float pitch = Random.Range(0.96f, 1.04f) * (1f + 0.06f * shot.Score) * (speed > 40f ? 1.04f : 1f);
            PlayAt(Pick(set), e.Point, volume, pitch);
        }

        void Update()
        {
            effectsVolume = GameSettings.EffectsVolume;
            crowdVolume = GameSettings.CrowdVolume;
            if (game == null || game.Session == null) return;
            RallySession session = game.Session;

            bool inPlay = session.State == SessionState.Rally || session.State == SessionState.Toss;
            float target = (inPlay ? murmurInPlay : murmurBetweenPoints) * crowdVolume;
            murmurSource.volume = Mathf.MoveTowards(murmurSource.volume, target, Time.deltaTime * 0.4f);

            for (int i = 0; i < 2; i++) CheckSqueak(session.Agent((PlayerSlot)i), i);
        }

        /// <summary>Shoes squeak when a player running fast turns or stops hard: compared with where they were heading a moment ago.</summary>
        void CheckSqueak(PlayerAgent agent, int index)
        {
            Vector3 velocity = agent.Movement.Velocity;
            Vector3 recent = lastVelocity[index];
            lastVelocity[index] = Vector3.Lerp(recent, velocity, Mathf.Clamp01(Time.deltaTime / 0.15f));
            if (recent.magnitude < SqueakMinSpeed || Time.time - lastSqueak[index] < SqueakCooldown) return;
            if (Vector3.Dot(recent.normalized, velocity) < recent.magnitude * 0.3f)
            {
                lastSqueak[index] = Time.time;
                PlayAt(Pick(squeak), agent.Position, 0.35f, Random.Range(0.92f, 1.1f));
            }
        }

        void PlayAt(AudioClip clip, Vector3 position, float volume, float pitch)
        {
            if (clip == null) return;
            AudioSource source = pool[nextVoice];
            nextVoice = (nextVoice + 1) % pool.Length;
            source.transform.position = position;
            source.pitch = pitch;
            source.PlayOneShot(clip, volume * effectsVolume);
        }

        void PlayCrowd(AudioClip clip, float volume)
        {
            if (clip == null) return;
            crowdSource.PlayOneShot(clip, volume * crowdVolume);
        }

        static AudioClip Pick(AudioClip[] clips) => clips == null || clips.Length == 0 ? null : clips[Random.Range(0, clips.Length)];
    }
}
