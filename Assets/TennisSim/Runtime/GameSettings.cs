using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TennisSim
{
    /// <summary>
    /// The player's settings: graphics quality, volumes, window mode and control bindings, kept between runs in
    /// PlayerPrefs. Graphics are applied to a copy of the render pipeline asset in the built game.
    /// </summary>
    public static class GameSettings
    {
        public enum Quality { Low, Medium, High }

        const string QualityKey = "settings.quality";
        const string EffectsKey = "settings.volume.effects";
        const string CrowdKey = "settings.volume.crowd";
        const string BindingsKey = "settings.bindings";

        struct Preset
        {
            public float ShadowDistance;
            public int ShadowResolution;
            public int Cascades;
            public int Msaa;
            public float RenderScale;
        }

        static readonly Preset[] Presets =
        {
            new Preset { ShadowDistance = 35f, ShadowResolution = 1024, Cascades = 2, Msaa = 1, RenderScale = 0.8f },
            new Preset { ShadowDistance = 55f, ShadowResolution = 2048, Cascades = 2, Msaa = 2, RenderScale = 1f },
            new Preset { ShadowDistance = 70f, ShadowResolution = 4096, Cascades = 4, Msaa = 4, RenderScale = 1f },
        };

        static UniversalRenderPipelineAsset runtimePipeline;

        public static Quality GraphicsQuality
        {
            get => (Quality)Mathf.Clamp(PlayerPrefs.GetInt(QualityKey, (int)Quality.High), 0, 2);
            set
            {
                PlayerPrefs.SetInt(QualityKey, (int)value);
                ApplyGraphics();
            }
        }

        static float? effectsVolume;
        static float? crowdVolume;

        public static float EffectsVolume
        {
            get => effectsVolume ??= PlayerPrefs.GetFloat(EffectsKey, 1f);
            set
            {
                effectsVolume = Mathf.Clamp01(value);
                PlayerPrefs.SetFloat(EffectsKey, effectsVolume.Value);
            }
        }

        public static float CrowdVolume
        {
            get => crowdVolume ??= PlayerPrefs.GetFloat(CrowdKey, 0.8f);
            set
            {
                crowdVolume = Mathf.Clamp01(value);
                PlayerPrefs.SetFloat(CrowdKey, crowdVolume.Value);
            }
        }

        public static bool Fullscreen
        {
            get => Screen.fullScreenMode != FullScreenMode.Windowed;
            set => Screen.fullScreenMode = value ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
        }

        /// <summary>The control rebinds, as the Input System's override JSON; empty for the defaults.</summary>
        public static string BindingOverrides
        {
            get => PlayerPrefs.GetString(BindingsKey, "");
            set => PlayerPrefs.SetString(BindingsKey, value ?? "");
        }

        public static void Save() => PlayerPrefs.Save();

        /// <summary>
        /// Applies the quality preset in a built game. In the editor swapping the pipeline would be written into the
        /// project's settings, so there the choice is only saved.
        /// </summary>
        public static void ApplyGraphics()
        {
            if (Application.isEditor) return;
            if (runtimePipeline == null)
            {
                var asset = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
                if (asset == null) return;
                runtimePipeline = Object.Instantiate(asset);
                runtimePipeline.name = asset.name + " (settings)";
                GraphicsSettings.defaultRenderPipeline = runtimePipeline;
                QualitySettings.renderPipeline = runtimePipeline;
            }

            Preset preset = Presets[(int)GraphicsQuality];
            runtimePipeline.shadowDistance = preset.ShadowDistance;
            runtimePipeline.mainLightShadowmapResolution = preset.ShadowResolution;
            runtimePipeline.shadowCascadeCount = preset.Cascades;
            runtimePipeline.msaaSampleCount = preset.Msaa;
            runtimePipeline.renderScale = preset.RenderScale;
        }
    }
}
