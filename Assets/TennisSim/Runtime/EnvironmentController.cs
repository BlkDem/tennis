using UnityEngine;
using UnityEngine.Rendering;

namespace TennisSim
{
    /// <summary>
    /// Day or evening: the sun's height, colour and strength, the ambient light, the sky, and the floodlights
    /// on the corner masts. Works on copies of the sky and lamp materials, so playing never edits the assets.
    /// </summary>
    public sealed class EnvironmentController : MonoBehaviour
    {
        static readonly int EmissionColour = Shader.PropertyToID("_EmissionColor");
        static readonly int Exposure = Shader.PropertyToID("_Exposure");

        public Light sun;
        public Light[] floodlights;
        public Renderer lampRenderer;
        public Material sky;

        [Header("Day")]
        public Vector3 daySun = new Vector3(58f, -35f, 0f);
        public Color daySunColour = new Color(1f, 0.97f, 0.92f);
        public float daySunIntensity = 1.05f;
        public Color dayAmbient = new Color(0.48f, 0.5f, 0.54f);
        public float daySkyExposure = 1.25f;

        [Header("Evening")]
        public Vector3 eveningSun = new Vector3(7f, -70f, 0f);
        public Color eveningSunColour = new Color(1f, 0.55f, 0.3f);
        public float eveningSunIntensity = 0.35f;
        [Tooltip("The stands get no floodlight, so this keeps the crowd readable while the court stays the brightest place.")]
        public Color eveningAmbient = new Color(0.3f, 0.31f, 0.4f);
        public float eveningSkyExposure = 0.75f;
        public Color lampGlow = new Color(1f, 0.95f, 0.85f) * 6f;

        Material skyInstance;
        Material lampInstance;

        public bool Evening { get; private set; }

        void Awake()
        {
            if (sky != null)
            {
                skyInstance = new Material(sky);
                RenderSettings.skybox = skyInstance;
            }
            if (lampRenderer != null) lampInstance = lampRenderer.material;
            Apply(false);
        }

        void OnDestroy()
        {
            if (skyInstance != null) Destroy(skyInstance);
            if (lampInstance != null) Destroy(lampInstance);
        }

        public void Apply(bool evening)
        {
            Evening = evening;
            sun.transform.rotation = Quaternion.Euler(evening ? eveningSun : daySun);
            sun.color = evening ? eveningSunColour : daySunColour;
            sun.intensity = evening ? eveningSunIntensity : daySunIntensity;

            Color ambient = evening ? eveningAmbient : dayAmbient;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = ambient;
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(ambient);
            RenderSettings.ambientProbe = probe;

            if (skyInstance != null) skyInstance.SetFloat(Exposure, evening ? eveningSkyExposure : daySkyExposure);
            foreach (Light light in floodlights) light.enabled = evening;
            if (lampInstance != null)
            {
                lampInstance.EnableKeyword("_EMISSION");
                lampInstance.SetColor(EmissionColour, evening ? lampGlow : Color.black);
            }
        }
    }
}
