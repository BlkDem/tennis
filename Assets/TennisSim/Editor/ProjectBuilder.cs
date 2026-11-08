using System.Collections.Generic;
using System.IO;
using System.Linq;
using TennisSim.Simulation;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TennisSim.EditorTools
{
    /// <summary>
    /// Generates the project's assets and the prototype scene from code, so the prototype can be rebuilt
    /// and reviewed as text. Art comes from Tools/Blender. Runs once by itself when the project is first opened.
    /// Run in batch with: -executeMethod TennisSim.EditorTools.ProjectBuilder.BuildAll
    /// </summary>
    [InitializeOnLoad]
    public static class ProjectBuilder
    {
        const string Root = "Assets/TennisSim";
        const string SettingsDir = Root + "/Settings";
        const string ConfigDir = Root + "/Config";
        const string MaterialDir = Root + "/Materials";
        const string GeneratedDir = Root + "/Generated";
        const string SceneDir = Root + "/Scenes";
        const string PlayerArtDir = Root + "/Art/Player";
        const string CourtArtDir = Root + "/Art/Court";
        const string StadiumArtDir = Root + "/Art/Stadium";
        const string UiFontPath = Root + "/Fonts/Inter-Regular.ttf";
        const string UiBoldFontPath = Root + "/Fonts/Inter-Bold.ttf";
        const string StadiumModelPath = StadiumArtDir + "/Stadium.fbx";
        const string StadiumLayoutPath = StadiumArtDir + "/Stadium_Layout.json";
        const string CrowdShaderPath = Root + "/Shaders/Crowd.shader";
        const string CrowdMeshPath = GeneratedDir + "/Crowd.mesh.asset";
        const string AudioDir = Root + "/Audio";
        const string ScenePath = SceneDir + "/Prototype.unity";
        const string UrpAssetPath = SettingsDir + "/URP.asset";
        const string UrpRendererPath = SettingsDir + "/URP_Renderer.asset";
        const string BuiltinRendererPath = "Assets/UniversalRenderer.asset";
        const string ControlsPath = SettingsDir + "/TennisControls.inputactions";
        const string WindowsPlayerPath = "Build/Windows/TennisSimulator.exe";
        const string WebGLPlayerPath = "Build/WebGL";
        const string PlayerModelPath = PlayerArtDir + "/Player.fbx";
        const string BallModelPath = PlayerArtDir + "/Ball.fbx";
        const string AnimatorPath = GeneratedDir + "/Player.controller";
        const string UpperBodyMaskPath = GeneratedDir + "/UpperBody.mask";
        const int InputSystemOnly = 1;

        // The surface the court texture covers; must match Tools/Blender/make_court_textures.py.
        const float SurfaceWidth = 18.29f;
        const float SurfaceLength = 36.58f;
        const float DetailTileMetres = 1.5f;
        const float SurfaceHeight = 0.002f;

        const float NetTapeHeight = 0.06f;
        const float BallVisualScale = 1.4f;

        /// <summary>Share of seats with someone in them.</summary>
        const float CrowdOccupancy = 0.88f;
        /// <summary>A spectator card's size; must match CELL_METRES_HIGH in Tools/Blender/make_crowd_atlas.py.</summary>
        static readonly Vector2 CrowdCardSize = new Vector2(1.2f, 2.4f);
        const int CrowdPoses = 5;

        static readonly string[] LoopingClips =
        {
            "Idle", "Ready", "Walk", "Run", "Sprint", "Backpedal", "StrafeLeft", "StrafeRight",
            "Forehand_Prep", "Backhand_Prep", "ForehandSlice_Prep", "BackhandSlice_Prep", "Volley_Prep", "Smash_Prep",
            "Serve_Ready",
        };

        static readonly string[] UpperBodyStates =
        {
            "Forehand_Prep", "Forehand_Swing", "Backhand_Prep", "Backhand_Swing",
            "ForehandSlice_Prep", "ForehandSlice_Swing", "BackhandSlice_Prep", "BackhandSlice_Swing",
            "Volley_Prep", "Volley_Swing", "Smash_Prep", "Smash_Swing", "Recovery",
        };

        /// <summary>Whole-body states the animation controller moves between itself, so they have no transitions.</summary>
        static readonly string[] ServeStates = { "Serve_Ready", "Serve_Toss", "Serve_Swing", "SplitStep" };

        static readonly string[] AllClips = LoopingClips
            .Concat(new[]
            {
                "Forehand_Swing", "Backhand_Swing", "ForehandSlice_Swing", "BackhandSlice_Swing", "Volley_Swing", "Smash_Swing",
                "Serve_Toss", "Serve_Swing", "SplitStep", "Recovery", "Celebrate", "PointLost",
            })
            .ToArray();

        static ProjectBuilder()
        {
            if (Application.isBatchMode || File.Exists(ScenePath)) return;
            EditorApplication.delayCall += BuildWhenReady;
        }

        static void BuildWhenReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += BuildWhenReady;
                return;
            }
            BuildAll();
            EditorSceneManager.OpenScene(ScenePath);
        }

        [MenuItem("Tennis/Rebuild Project Assets and Scene")]
        public static void BuildAll()
        {
            foreach (string dir in new[] { SettingsDir, ConfigDir, MaterialDir, GeneratedDir, SceneDir })
                Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();

            ConfigureProject();
            ConfigureRenderPipeline();
            ConfigureArtImports();
            Materials materials = BuildMaterials();
            BuildScene(materials);

            AssetDatabase.SaveAssets();
            Debug.Log("[Tennis] Project assets and scene are built.");
        }

        /// <summary>Run in batch with: -executeMethod TennisSim.EditorTools.ProjectBuilder.BuildWindowsPlayer</summary>
        [MenuItem("Tennis/Build Windows Player")]
        public static void BuildWindowsPlayer()
        {
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = WindowsPlayerPath,
                target = BuildTarget.StandaloneWindows64,
            };

            BuildSummary summary = BuildPipeline.BuildPlayer(options).summary;
            if (summary.result != BuildResult.Succeeded)
                throw new System.InvalidOperationException($"[Tennis] Player build failed: {summary.result}, {summary.totalErrors} errors.");

            Debug.Log("[Tennis] Player built: " + Path.GetFullPath(WindowsPlayerPath));
        }

        /// <summary>
        /// The browser version, into Build/WebGL. Gzip with decompression in the loader, so any static web server
        /// can serve it without special headers. Run in batch with:
        /// -executeMethod TennisSim.EditorTools.ProjectBuilder.BuildWebGL
        /// </summary>
        [MenuItem("Tennis/Build WebGL")]
        public static void BuildWebGL()
        {
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.template = "PROJECT:Tennis";
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.WebGL.nameFilesAsHashes = false;

            string output = WebGLPlayerPath;
            if (Directory.Exists(output)) Directory.Delete(output, true);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = BuildTarget.WebGL,
            };

            BuildSummary summary = BuildPipeline.BuildPlayer(options).summary;
            if (summary.result != BuildResult.Succeeded)
                throw new System.InvalidOperationException($"[Tennis] WebGL build failed: {summary.result}, {summary.totalErrors} errors.");

            Debug.Log($"[Tennis] WebGL built: {Path.GetFullPath(output)}, {summary.totalSize / (1024f * 1024f):F1} MB");
        }

        static void ConfigureProject()
        {
            PlayerSettings.productName = "Tennis Simulator";
            PlayerSettings.colorSpace = ColorSpace.Linear;

            // Fullscreen at the monitor's own resolution; Alt+Enter switches to a resizable window.
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
            PlayerSettings.defaultIsNativeResolution = true;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.allowFullscreenSwitch = true;
            PlayerSettings.defaultScreenWidth = 1920;
            PlayerSettings.defaultScreenHeight = 1080;

            Object[] playerSettings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (playerSettings.Length == 0) return;

            var serialized = new SerializedObject(playerSettings[0]);
            SerializedProperty handler = serialized.FindProperty("activeInputHandler");
            if (handler != null && handler.intValue != InputSystemOnly)
            {
                handler.intValue = InputSystemOnly;
                serialized.ApplyModifiedProperties();
                Debug.LogWarning("[Tennis] Switched the project to the new Input System. Restart the editor for it to take effect.");
            }
        }

        static void ConfigureRenderPipeline()
        {
            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (urp == null)
            {
                urp = UniversalRenderPipelineAsset.Create();
                AssetDatabase.CreateAsset(urp, UrpAssetPath);
                urp.LoadBuiltinRendererData();
                AssetDatabase.SaveAssets();
                string moveError = AssetDatabase.MoveAsset(BuiltinRendererPath, UrpRendererPath);
                if (!string.IsNullOrEmpty(moveError)) Debug.LogError("[Tennis] Could not move the URP renderer: " + moveError);
            }

            urp.msaaSampleCount = 4;
            // Sharp player shadows near the camera, and the stands' shadows out to the far end, over four high-resolution cascades.
            urp.shadowDistance = 70f;
            urp.shadowCascadeCount = 4;
            urp.mainLightShadowmapResolution = 4096;
            EditorUtility.SetDirty(urp);

            GraphicsSettings.defaultRenderPipeline = urp;
            for (int level = 0; level < QualitySettings.names.Length; level++)
            {
                QualitySettings.SetQualityLevel(level, false);
                QualitySettings.renderPipeline = urp;
            }
        }

        // ---------------------------------------------------------------- Importing art

        static void ConfigureArtImports()
        {
            ConfigureTexture($"{PlayerArtDir}/Player_BaseColor.png", true, false, 2048);
            ConfigureTexture($"{PlayerArtDir}/Player_BaseColor_Red.png", true, false, 2048);
            ConfigureTexture($"{PlayerArtDir}/Player_Normal.png", false, true, 2048);
            ConfigureTexture($"{PlayerArtDir}/Player_Mask.png", false, false, 2048);
            ConfigureTexture($"{PlayerArtDir}/Racket_BaseColor.png", true, false, 1024);
            ConfigureTexture($"{PlayerArtDir}/Racket_Normal.png", false, true, 1024);
            ConfigureTexture($"{PlayerArtDir}/Racket_Mask.png", false, false, 1024);
            ConfigureTexture($"{PlayerArtDir}/Racket_Strings.png", true, false, 64, TextureWrapMode.Repeat, keepAlpha: true);
            ConfigureTexture($"{CourtArtDir}/Court_BaseColor.png", true, false, 4096, anisotropy: 8);
            ConfigureTexture($"{CourtArtDir}/Court_Mask.png", false, false, 2048, anisotropy: 4);
            ConfigureTexture($"{CourtArtDir}/Court_Detail_Albedo.png", false, false, 1024, TextureWrapMode.Repeat, anisotropy: 4);
            ConfigureTexture($"{CourtArtDir}/Court_Detail_Normal.png", false, true, 1024, TextureWrapMode.Repeat, anisotropy: 4);
            ConfigureTexture($"{StadiumArtDir}/Stadium_Boards.png", true, false, 2048, anisotropy: 8);
            ConfigureTexture($"{StadiumArtDir}/Concrete_BaseColor.png", true, false, 512, TextureWrapMode.Repeat, anisotropy: 4);
            // Mipmaps keep each spectator's outline as the cards shrink into the distance.
            ConfigureTexture($"{StadiumArtDir}/Crowd_Atlas.png", true, false, 1024, keepAlpha: true, preserveCoverage: true);
            ConfigureTexture($"{StadiumArtDir}/Crowd_Mask.png", false, false, 1024);

            ConfigurePlayerModel();
            ConfigureBallModel();
            ConfigureStaticModel(StadiumModelPath);
        }

        static void ConfigureTexture(string path, bool sRgb, bool normalMap, int maxSize, TextureWrapMode wrap = TextureWrapMode.Clamp,
            int anisotropy = 1, bool keepAlpha = false, bool preserveCoverage = false)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) throw new FileNotFoundException("Texture not found: " + path);

            importer.textureType = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = sRgb;
            importer.maxTextureSize = maxSize;
            importer.wrapMode = wrap;
            importer.anisoLevel = anisotropy;
            importer.mipmapEnabled = true;
            importer.alphaIsTransparency = keepAlpha;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipMapsPreserveCoverage = preserveCoverage;
            importer.alphaTestReferenceValue = 0.5f;
            importer.SaveAndReimport();
        }

        static void ConfigureStaticModel(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) throw new FileNotFoundException("Model not found: " + path);
            importer.animationType = ModelImporterAnimationType.None;
            importer.importAnimation = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.indexFormat = ModelImporterIndexFormat.Auto;
            importer.SaveAndReimport();
        }

        static void ConfigurePlayerModel()
        {
            var importer = AssetImporter.GetAtPath(PlayerModelPath) as ModelImporter;
            if (importer == null) throw new FileNotFoundException("Model not found: " + PlayerModelPath);

            // Generic keeps the racket bone and every joint exact; a clip set for another model just needs the same names.
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.importAnimation = true;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
            importer.SaveAndReimport();

            // Blender prefixes takes with the armature's name, and exports a take for each action.
            var clips = new List<ModelImporterClipAnimation>();
            foreach (ModelImporterClipAnimation source in importer.defaultClipAnimations)
            {
                string name = source.name.Contains("|") ? source.name.Substring(source.name.LastIndexOf('|') + 1) : source.name;
                if (!AllClips.Contains(name)) continue;
                source.name = name;
                source.loopTime = LoopingClips.Contains(name);
                source.loopPose = false;
                clips.Add(source);
            }

            string[] missing = AllClips.Except(clips.Select(c => c.name)).ToArray();
            if (missing.Length > 0) throw new FileNotFoundException("Player.fbx lacks clips: " + string.Join(", ", missing));

            importer.clipAnimations = clips.ToArray();
            importer.SaveAndReimport();
        }

        static void ConfigureBallModel()
        {
            var importer = AssetImporter.GetAtPath(BallModelPath) as ModelImporter;
            if (importer == null) throw new FileNotFoundException("Model not found: " + BallModelPath);
            importer.animationType = ModelImporterAnimationType.None;
            importer.importAnimation = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.SaveAndReimport();
        }

        // ---------------------------------------------------------------- Materials

        sealed class Materials
        {
            public Material Ground, Court, Post, NetFabric, NetTape;
            public Material Ball, BallShadow, LandingMarker;
            public Material NearPlayer, FarPlayer, Racket, RacketStrings;
            public Material Crowd;
            /// <summary>Stadium materials by the name of the FBX part they belong to.</summary>
            public Dictionary<string, Material> Stadium;
        }

        static Materials BuildMaterials() => new Materials
        {
            Ground = Lit("Ground", new Color(0.32f, 0.32f, 0.31f), 0.1f),
            Court = CourtSurface(),
            Stadium = new Dictionary<string, Material>
            {
                ["Stadium_Concrete"] = Concrete("StadiumConcrete", new Color(0.62f, 0.62f, 0.6f)),
                ["Stadium_Steps"] = Concrete("StadiumSteps", new Color(0.72f, 0.72f, 0.7f)),
                ["Stadium_Facade"] = Concrete("StadiumFacade", new Color(0.55f, 0.56f, 0.56f)),
                ["Stadium_Railing"] = Lit("StadiumRailing", new Color(0.05f, 0.17f, 0.11f), 0.3f),
                ["Stadium_Accent"] = Lit("StadiumAccent", new Color(0.95f, 0.75f, 0.1f), 0.3f),
                ["Stadium_Boards"] = Board(),
                ["Seats_Blue"] = Lit("SeatsBlue", new Color(0.04f, 0.11f, 0.32f), 0.55f),
                ["Seats_White"] = Lit("SeatsWhite", new Color(0.86f, 0.87f, 0.88f), 0.55f),
                ["Furniture_Metal"] = Lit("FurnitureMetal", new Color(0.05f, 0.2f, 0.13f), 0.45f),
                ["Furniture_Seat"] = Lit("FurnitureSeat", new Color(0.9f, 0.9f, 0.88f), 0.4f),
                ["Scoreboard_Frame"] = Lit("ScoreboardFrame", new Color(0.12f, 0.13f, 0.14f), 0.5f),
                ["Scoreboard_Screen"] = Lit("ScoreboardScreen", new Color(0.015f, 0.02f, 0.03f), 0.8f),
                ["Floodlight_Metal"] = Lit("FloodlightMetal", new Color(0.32f, 0.34f, 0.36f), 0.5f),
                ["Floodlight_Lamp"] = Lit("FloodlightLamp", new Color(0.85f, 0.86f, 0.8f), 0.8f),
            },
            Crowd = CrowdMaterial(),
            Post = Lit("NetPost", new Color(0.12f, 0.14f, 0.13f), 0.5f),
            NetFabric = Transparent("NetFabric", NetTexture(), Color.white),
            NetTape = DoubleSided(Lit("NetTape", Color.white, 0.2f)),
            Ball = Textured("Ball", PlayerArtDir, "Racket_BaseColor", "Racket_Normal", "Racket_Mask"),
            BallShadow = Transparent("BallShadow", BlobTexture(), new Color(0f, 0f, 0f, 0.55f)),
            LandingMarker = Transparent("LandingMarker", RingTexture(), new Color(1f, 0.9f, 0.2f, 0.55f)),
            NearPlayer = Textured("PlayerNear", PlayerArtDir, "Player_BaseColor", "Player_Normal", "Player_Mask"),
            FarPlayer = Textured("PlayerFar", PlayerArtDir, "Player_BaseColor_Red", "Player_Normal", "Player_Mask"),
            Racket = Textured("Racket", PlayerArtDir, "Racket_BaseColor", "Racket_Normal", "Racket_Mask"),
            RacketStrings = Transparent("RacketStrings", Load<Texture2D>($"{PlayerArtDir}/Racket_Strings.png"), new Color(0.92f, 0.92f, 0.88f, 1f)),
        };

        static T Load<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new FileNotFoundException("Asset not found: " + path);
            return asset;
        }

        static Material Lit(string name, Color color, float smoothness)
        {
            Material material = LoadOrCreate(name, "Universal Render Pipeline/Lit");
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material Textured(string name, string directory, string baseMap, string normalMap, string mask)
        {
            Material material = LoadOrCreate(name, "Universal Render Pipeline/Lit");
            material.SetColor("_BaseColor", Color.white);
            material.SetTexture("_BaseMap", Load<Texture2D>($"{directory}/{baseMap}.png"));
            AddSurfaceMaps(material, $"{directory}/{normalMap}.png", $"{directory}/{mask}.png");
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>The stadium's painted concrete: one light tiling texture, tinted per surface. The FBX's UVs are in 2 m units.</summary>
        static Material Concrete(string name, Color tint)
        {
            Material material = LoadOrCreate(name, "Universal Render Pipeline/Lit");
            material.SetTexture("_BaseMap", Load<Texture2D>($"{StadiumArtDir}/Concrete_BaseColor.png"));
            material.SetColor("_BaseColor", tint);
            material.SetFloat("_Smoothness", 0.12f);
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material Board()
        {
            Material material = LoadOrCreate("StadiumBoards", "Universal Render Pipeline/Lit");
            material.SetTexture("_BaseMap", Load<Texture2D>($"{StadiumArtDir}/Stadium_Boards.png"));
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", 0.35f);
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material CrowdMaterial()
        {
            string path = $"{MaterialDir}/Crowd.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Load<Shader>(CrowdShaderPath));
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetTexture("_Atlas", Load<Texture2D>($"{StadiumArtDir}/Crowd_Atlas.png"));
            material.SetTexture("_Mask", Load<Texture2D>($"{StadiumArtDir}/Crowd_Mask.png"));
            material.SetVector("_Size", CrowdCardSize);
            material.SetVector("_Grid", new Vector4(CrowdPoses, 2f, 0f, 0f));
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material CourtSurface()
        {
            Material material = LoadOrCreate("CourtSurface", "Universal Render Pipeline/Lit");
            material.SetColor("_BaseColor", Color.white);
            material.SetTexture("_BaseMap", Load<Texture2D>($"{CourtArtDir}/Court_BaseColor.png"));
            AddSurfaceMaps(material, null, $"{CourtArtDir}/Court_Mask.png");

            // The grit up close: tiled at true size over a mesh whose UVs span the whole surface.
            material.SetTexture("_DetailAlbedoMap", Load<Texture2D>($"{CourtArtDir}/Court_Detail_Albedo.png"));
            material.SetTexture("_DetailNormalMap", Load<Texture2D>($"{CourtArtDir}/Court_Detail_Normal.png"));
            material.SetTextureScale("_DetailAlbedoMap", new Vector2(SurfaceWidth / DetailTileMetres, SurfaceLength / DetailTileMetres));
            material.SetFloat("_DetailAlbedoMapScale", 1f);
            material.SetFloat("_DetailNormalMapScale", 0.6f);
            material.EnableKeyword("_DETAIL_MULX2");
            EditorUtility.SetDirty(material);
            return material;
        }

        static void AddSurfaceMaps(Material material, string normalPath, string maskPath)
        {
            if (normalPath != null)
            {
                material.SetTexture("_BumpMap", Load<Texture2D>(normalPath));
                material.SetFloat("_BumpScale", 1f);
                material.EnableKeyword("_NORMALMAP");
            }
            material.SetTexture("_MetallicGlossMap", Load<Texture2D>(maskPath));
            material.SetFloat("_Metallic", 1f);
            material.SetFloat("_Smoothness", 1f);
            material.SetFloat("_SmoothnessTextureChannel", 0f);
            material.EnableKeyword("_METALLICSPECGLOSSMAP");
        }

        static Material DoubleSided(Material material)
        {
            material.SetFloat("_Cull", (float)CullMode.Off);
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material Transparent(string name, Texture2D texture, Color color)
        {
            Material material = LoadOrCreate(name, "Universal Render Pipeline/Unlit");
            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material LoadOrCreate(string name, string shaderName)
        {
            string path = $"{MaterialDir}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;

            Shader shader = Shader.Find(shaderName);
            if (shader == null) throw new FileNotFoundException("Shader not found: " + shaderName);
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        // ---------------------------------------------------------------- Procedural textures

        static Texture2D NetTexture()
        {
            const int Size = 32;
            const int Cord = 3;
            return SaveTexture("NetFabric", Size, TextureWrapMode.Repeat, (x, y) =>
                x < Cord || y < Cord ? new Color(0.05f, 0.05f, 0.05f, 0.95f) : new Color(0f, 0f, 0f, 0f));
        }

        static Texture2D BlobTexture()
        {
            const int Size = 64;
            return SaveTexture("Blob", Size, TextureWrapMode.Clamp, (x, y) =>
            {
                float r = RadiusFromCentre(x, y, Size);
                return new Color(1f, 1f, 1f, Mathf.Clamp01(1f - r * r));
            });
        }

        static Texture2D RingTexture()
        {
            const int Size = 64;
            return SaveTexture("Ring", Size, TextureWrapMode.Clamp, (x, y) =>
            {
                float r = RadiusFromCentre(x, y, Size);
                return new Color(1f, 1f, 1f, Mathf.Clamp01(1f - Mathf.Abs(r - 0.8f) * 8f));
            });
        }

        static float RadiusFromCentre(int x, int y, int size)
        {
            float half = (size - 1) * 0.5f;
            return new Vector2((x - half) / half, (y - half) / half).magnitude;
        }

        static Texture2D SaveTexture(string name, int size, TextureWrapMode wrap, System.Func<int, int, Color> pixel)
        {
            string path = $"{GeneratedDir}/{name}.asset";
            AssetDatabase.DeleteAsset(path);

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = wrap, filterMode = FilterMode.Trilinear, name = name };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    texture.SetPixel(x, y, pixel(x, y));
            texture.Apply(true);
            AssetDatabase.CreateAsset(texture, path);
            return texture;
        }

        // ---------------------------------------------------------------- Animator

        static AnimatorController BuildAnimator()
        {
            var clips = AssetDatabase.LoadAllAssetsAtPath(PlayerModelPath).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__"))
                .ToDictionary(c => c.name);

            AssetDatabase.DeleteAsset(AnimatorPath);
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(AnimatorPath);
            controller.AddParameter("MoveX", AnimatorControllerParameterType.Float);
            controller.AddParameter("MoveZ", AnimatorControllerParameterType.Float);
            controller.AddParameter("Engaged", AnimatorControllerParameterType.Bool);
            controller.AddParameter(new AnimatorControllerParameter { name = "SwingSpeed", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });

            AnimatorStateMachine body = controller.layers[0].stateMachine;

            AnimatorState locomotion = controller.CreateBlendTreeInController("Locomotion", out BlendTree tree, 0);
            tree.blendType = BlendTreeType.FreeformDirectional2D;
            tree.blendParameter = "MoveX";
            tree.blendParameterY = "MoveZ";
            tree.useAutomaticThresholds = false;
            // Positions are the speeds the clips were authored at, in m/s, so feet stay planted at runtime.
            tree.AddChild(clips["Ready"], Vector2.zero);
            tree.AddChild(clips["Walk"], new Vector2(0f, 2f));
            tree.AddChild(clips["Run"], new Vector2(0f, 6.2f));
            tree.AddChild(clips["Sprint"], new Vector2(0f, 7.8f));
            tree.AddChild(clips["Backpedal"], new Vector2(0f, -4f));
            tree.AddChild(clips["StrafeRight"], new Vector2(5f, 0f));
            tree.AddChild(clips["StrafeLeft"], new Vector2(-5f, 0f));
            body.defaultState = locomotion;

            AnimatorState idle = AddState(body, "Idle", clips);
            AnimatorStateTransition relax = locomotion.AddTransition(idle);
            relax.hasExitTime = false;
            relax.duration = 0.3f;
            relax.AddCondition(AnimatorConditionMode.IfNot, 0f, "Engaged");
            AnimatorStateTransition engage = idle.AddTransition(locomotion);
            engage.hasExitTime = false;
            engage.duration = 0.15f;
            engage.AddCondition(AnimatorConditionMode.If, 0f, "Engaged");

            foreach (string name in new[] { "Celebrate", "PointLost" })
            {
                AnimatorState state = AddState(body, name, clips);
                AnimatorStateTransition done = state.AddTransition(locomotion);
                done.hasExitTime = true;
                done.exitTime = 0.95f;
                done.duration = 0.2f;
            }
            foreach (string name in ServeStates)
            {
                AnimatorState state = AddState(body, name, clips);
                if (name == "Serve_Swing")
                {
                    state.speedParameter = "SwingSpeed";
                    state.speedParameterActive = true;
                }
            }

            var upperLayer = new AnimatorControllerLayer
            {
                name = "UpperBody",
                defaultWeight = 0f,
                avatarMask = BuildUpperBodyMask(),
                stateMachine = new AnimatorStateMachine { name = "UpperBody", hideFlags = HideFlags.HideInHierarchy },
            };
            AssetDatabase.AddObjectToAsset(upperLayer.stateMachine, AnimatorPath);
            controller.AddLayer(upperLayer);

            AnimatorState neutral = upperLayer.stateMachine.AddState("Neutral");
            neutral.motion = clips["Ready"];
            upperLayer.stateMachine.defaultState = neutral;
            foreach (string name in UpperBodyStates)
            {
                AnimatorState state = AddState(upperLayer.stateMachine, name, clips);
                if (name.EndsWith("_Swing"))
                {
                    state.speedParameter = "SwingSpeed";
                    state.speedParameterActive = true;
                }
            }

            EditorUtility.SetDirty(controller);
            return controller;
        }

        static AnimatorState AddState(AnimatorStateMachine machine, string name, Dictionary<string, AnimationClip> clips)
        {
            AnimatorState state = machine.AddState(name);
            state.motion = clips[name];
            return state;
        }

        /// <summary>The chest and arms play the shot while the legs keep running.</summary>
        static AvatarMask BuildUpperBodyMask()
        {
            AssetDatabase.DeleteAsset(UpperBodyMaskPath);
            var mask = new AvatarMask();
            var instance = (GameObject)Object.Instantiate(Load<GameObject>(PlayerModelPath));
            mask.AddTransformPath(instance.transform, true);
            Object.DestroyImmediate(instance);

            int active = 0;
            for (int i = 0; i < mask.transformCount; i++)
            {
                bool upper = mask.GetTransformPath(i).Contains("Hips/Spine");
                mask.SetTransformActive(i, upper);
                if (upper) active++;
            }
            if (active == 0) throw new System.InvalidOperationException("The upper body mask matched no bones.");

            AssetDatabase.CreateAsset(mask, UpperBodyMaskPath);
            return mask;
        }

        // ---------------------------------------------------------------- Scene

        static void BuildScene(Materials m)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.48f, 0.5f, 0.54f);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.05f;
            sun.color = new Color(1f, 0.97f, 0.92f);
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(58f, -35f, 0f);
            RenderSettings.sun = sun;
            RenderSettings.skybox = DaySky();

            BuildCourt(m);

            BallController ball = BuildBall(m);
            AnimatorController animator = BuildAnimator();
            PlayerController near = BuildPlayer("Player Near", m.NearPlayer, m, animator, ball);
            PlayerController far = BuildPlayer("Player Far", m.FarPlayer, m, animator, ball);

            var inputManager = new GameObject("InputManager").AddComponent<InputManager>();
            inputManager.actions = Load<InputActionAsset>(ControlsPath);

            var game = new GameObject("GameManager").AddComponent<GameManager>();
            game.ballConfig = LoadOrCreateConfig<BallPhysicsConfig>("BallPhysicsConfig");
            game.shotConfig = LoadOrCreateConfig<ShotConfig>("ShotConfig");
            game.playerConfig = LoadOrCreateConfig<PlayerConfig>("PlayerConfig");
            game.inputManager = inputManager;
            game.ball = ball;
            game.nearPlayer = near;
            game.farPlayer = far;

            BuildStadium(m, game, sun);
            BuildAudio(game);

            new GameObject("PrototypeOverlay").AddComponent<PrototypeOverlay>().game = game;
            var hudController = new GameObject("HUD").AddComponent<HudController>();
            hudController.game = game;
            hudController.font = UiFont();

            BuildCamera(near, far, ball);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        /// <summary>Sound from Tools/Audio/make_sounds.py; variants of one sound share a name with a number after it.</summary>
        static void BuildAudio(GameManager game)
        {
            var audio = new GameObject("AudioManager").AddComponent<AudioManager>();
            audio.game = game;
            audio.hitHard = Clips("HitHard");
            audio.hitSoft = Clips("HitSoft");
            audio.hitFrame = Clips("HitFrame");
            audio.bounce = Clips("Bounce");
            audio.squeak = Clips("Squeak");
            audio.net = Load<AudioClip>($"{AudioDir}/Net.wav");
            audio.netCord = Load<AudioClip>($"{AudioDir}/NetCord.wav");
            audio.murmur = Load<AudioClip>($"{AudioDir}/CrowdMurmur.wav");
            audio.applause = Load<AudioClip>($"{AudioDir}/Applause.wav");
            audio.cheer = Load<AudioClip>($"{AudioDir}/Cheer.wav");
            audio.ooh = Load<AudioClip>($"{AudioDir}/Ooh.wav");
        }

        static AudioClip[] Clips(string prefix)
        {
            AudioClip[] clips = Directory.GetFiles(AudioDir, prefix + "_*.wav").OrderBy(p => p)
                .Select(p => Load<AudioClip>(p.Replace('\\', '/'))).ToArray();
            if (clips.Length == 0) throw new FileNotFoundException("No sounds named " + prefix);
            return clips;
        }

        static Material DaySky()
        {
            Material sky = LoadOrCreate("DaySky", "Skybox/Procedural");
            sky.SetFloat("_SunSize", 0.035f);
            sky.SetFloat("_AtmosphereThickness", 0.85f);
            sky.SetColor("_SkyTint", new Color(0.45f, 0.55f, 0.7f));
            sky.SetColor("_GroundColor", new Color(0.35f, 0.36f, 0.36f));
            sky.SetFloat("_Exposure", 1.25f);
            EditorUtility.SetDirty(sky);
            return sky;
        }

        static T LoadOrCreateConfig<T>(string name) where T : ScriptableObject
        {
            string path = $"{ConfigDir}/{name}.asset";
            var config = AssetDatabase.LoadAssetAtPath<T>(path);
            if (config != null) return config;
            config = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(config, path);
            return config;
        }

        static void BuildCourt(Materials m)
        {
            Transform court = new GameObject("Court").transform;

            Slab("Ground", court, m.Ground, new Vector3(120f, 0.1f, 140f), new Vector3(0f, -0.05f, 0f));
            BuildCourtSurface(court, m.Court);

            foreach (float sign in new[] { -1f, 1f })
            {
                Post("Net Post", court, m.Post, sign * CourtGeometry.NetHalfWidth, CourtGeometry.NetPostHeight, 0.06f);
                Post("Singles Stick", court, m.Post, sign * CourtGeometry.SinglesStickX, CourtGeometry.NetPostHeight, 0.03f);
            }

            BuildNetRibbon("Net Fabric", court, m.NetFabric, null, 0.1f);
            BuildNetRibbon("Net Tape", court, m.NetTape, NetTapeHeight, 1f);
        }

        /// <summary>One quad with its UVs spanning the surface: lines, colours and wear are all painted into the texture.</summary>
        static void BuildCourtSurface(Transform parent, Material material)
        {
            string path = $"{GeneratedDir}/CourtSurface.mesh.asset";
            AssetDatabase.DeleteAsset(path);

            float x = SurfaceWidth * 0.5f;
            float z = SurfaceLength * 0.5f;
            var mesh = new Mesh
            {
                name = "Court Surface",
                vertices = new[] { new Vector3(-x, 0f, -z), new Vector3(-x, 0f, z), new Vector3(x, 0f, z), new Vector3(x, 0f, -z) },
                uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
            };
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, path);

            var surface = new GameObject("Court Surface");
            surface.transform.SetParent(parent, false);
            surface.transform.position = new Vector3(0f, SurfaceHeight, 0f);
            surface.AddComponent<MeshFilter>().sharedMesh = mesh;
            surface.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        // ---------------------------------------------------------------- Stadium

        /// <summary>What Tools/Blender/make_stadium.py writes next to the model.</summary>
        [System.Serializable]
        sealed class StadiumLayout
        {
            public int seatStride;
            /// <summary>Per seat: x, y, z of the seat on its row's floor, then the x and z of the way it faces.</summary>
            public float[] seats;
            public float[] scoreboardCenter;
            public float scoreboardWidth;
            public float scoreboardHeight;
            /// <summary>Per floodlight: x, y, z of the lamp's face. Each aims at the centre of the court.</summary>
            public float[] floodlights;
        }

        const float FloodlightIntensity = 700f;
        const float FloodlightAngle = 62f;

        static void BuildStadium(Materials m, GameManager game, Light sun)
        {
            var stadium = (GameObject)PrefabUtility.InstantiatePrefab(Load<GameObject>(StadiumModelPath));
            stadium.name = "Stadium";
            foreach (MeshRenderer renderer in stadium.GetComponentsInChildren<MeshRenderer>())
            {
                if (!m.Stadium.TryGetValue(renderer.name, out Material material))
                    throw new System.ArgumentException("Unexpected stadium part: " + renderer.name);
                renderer.sharedMaterial = material;
                // The parapet's sponsor boards stand right behind the court; their shadow would only be noise.
                if (renderer.name == "Stadium_Accent") renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
            GameObjectUtility.SetStaticEditorFlags(stadium, StaticEditorFlags.BatchingStatic);
            foreach (Transform child in stadium.GetComponentsInChildren<Transform>())
                GameObjectUtility.SetStaticEditorFlags(child.gameObject, StaticEditorFlags.BatchingStatic);

            StadiumLayout layout = JsonUtility.FromJson<StadiumLayout>(Load<TextAsset>(StadiumLayoutPath).text);
            BuildCrowd(layout, m.Crowd);
            BuildScoreboard(layout, game);
            game.environment = BuildEnvironment(layout, sun, stadium.transform.Find("Floodlight_Lamp").GetComponent<Renderer>());
        }

        /// <summary>Day and evening. The floodlights hang on the corner masts and aim at the middle of the court.</summary>
        static EnvironmentController BuildEnvironment(StadiumLayout layout, Light sun, Renderer lamps)
        {
            var environment = new GameObject("Environment").AddComponent<EnvironmentController>();
            environment.sun = sun;
            environment.sky = RenderSettings.skybox;
            environment.lampRenderer = lamps;

            var lights = new List<Light>();
            for (int i = 0; i + 3 <= layout.floodlights.Length; i += 3)
            {
                var position = new Vector3(layout.floodlights[i], layout.floodlights[i + 1], layout.floodlights[i + 2]);
                var light = new GameObject("Floodlight " + lights.Count).AddComponent<Light>();
                light.transform.SetParent(environment.transform, false);
                light.transform.position = position;
                light.transform.rotation = Quaternion.LookRotation(Vector3.zero - position);
                light.type = LightType.Spot;
                light.spotAngle = FloodlightAngle;
                light.innerSpotAngle = FloodlightAngle * 0.6f;
                light.range = 120f;
                light.intensity = FloodlightIntensity;
                light.color = new Color(1f, 0.96f, 0.88f);
                light.shadows = LightShadows.None;
                light.enabled = false;
                lights.Add(light);
            }
            environment.floodlights = lights.ToArray();
            return environment;
        }

        /// <summary>
        /// One mesh for the whole crowd. Every spectator is four vertices on their seat; the crowd shader turns
        /// them into a card facing the camera. Who turns up, and what they wear, is fixed per seat.
        /// </summary>
        static void BuildCrowd(StadiumLayout layout, Material material)
        {
            var random = new System.Random(20261107);
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var corners = new List<Vector2>();
            var seeds = new List<Vector4>();
            var triangles = new List<int>();
            Vector2[] cardCorners = { new Vector2(-0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(-0.5f, 1f) };

            int stride = layout.seatStride;
            for (int i = 0; i + stride <= layout.seats.Length; i += stride)
            {
                if (random.NextDouble() > CrowdOccupancy) continue;
                var seat = new Vector3(layout.seats[i], layout.seats[i + 1], layout.seats[i + 2]);
                var facing = new Vector3(layout.seats[i + 3], 0f, layout.seats[i + 4]);
                var seed = new Vector4((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());

                int start = positions.Count;
                foreach (Vector2 corner in cardCorners)
                {
                    positions.Add(seat);
                    normals.Add(facing);
                    corners.Add(corner);
                    seeds.Add(seed);
                }
                triangles.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
            }

            AssetDatabase.DeleteAsset(CrowdMeshPath);
            var mesh = new Mesh { name = "Crowd", indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, corners);
            mesh.SetUVs(1, seeds);
            mesh.SetTriangles(triangles, 0);
            // The vertices only mark seats: grow the bounds by a card so the crowd is never culled while in view.
            mesh.RecalculateBounds();
            Bounds bounds = mesh.bounds;
            bounds.Expand(new Vector3(CrowdCardSize.x, CrowdCardSize.y * 2f, CrowdCardSize.x));
            mesh.bounds = bounds;
            AssetDatabase.CreateAsset(mesh, CrowdMeshPath);

            var crowd = new GameObject("Crowd");
            crowd.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = crowd.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            crowd.AddComponent<CrowdController>();
            Debug.Log($"[Tennis] Crowd: {positions.Count / 4} spectators in {layout.seats.Length / stride} seats.");
        }

        /// <summary>Text on the scoreboard's screen, laid out as columns: name, sets, games, points.</summary>
        static void BuildScoreboard(StadiumLayout layout, GameManager game)
        {
            var root = new GameObject("Scoreboard Display");
            root.transform.position = new Vector3(layout.scoreboardCenter[0], layout.scoreboardCenter[1], layout.scoreboardCenter[2]);
            var scoreboard = root.AddComponent<Scoreboard>();
            scoreboard.game = game;

            float w = layout.scoreboardWidth;
            float h = layout.scoreboardHeight;
            float rowHeight = h * 0.3f;
            var white = new Color(0.95f, 0.95f, 0.92f);
            var yellow = new Color(1f, 0.82f, 0.2f);
            for (int i = 0; i < 2; i++)
            {
                float y = h * 0.5f - h * 0.1f - rowHeight * (i + 0.5f);
                float text = rowHeight * 0.75f;
                scoreboard.serving[i] = ScoreText(root.transform, "Serving", new Vector3(-w * 0.45f, y), text * 0.6f, TextAnchor.MiddleCenter, yellow);
                scoreboard.names[i] = ScoreText(root.transform, "Name", new Vector3(-w * 0.41f, y), text, TextAnchor.MiddleLeft, white);
                scoreboard.sets[i] = ScoreText(root.transform, "Sets", new Vector3(w * 0.17f, y), text, TextAnchor.MiddleCenter, white);
                scoreboard.games[i] = ScoreText(root.transform, "Games", new Vector3(w * 0.28f, y), text, TextAnchor.MiddleCenter, white);
                scoreboard.points[i] = ScoreText(root.transform, "Points", new Vector3(w * 0.41f, y), text, TextAnchor.MiddleCenter, yellow);
            }
            scoreboard.info = ScoreText(root.transform, "Info", new Vector3(0f, -h * 0.36f), rowHeight * 0.5f, TextAnchor.MiddleCenter, yellow);
        }

        static TextMesh ScoreText(Transform parent, string name, Vector3 localPosition, float height, TextAnchor anchor, Color colour)
        {
            const int FontSize = 96;
            var textObject = new GameObject(name);
            textObject.transform.SetParent(parent, false);
            textObject.transform.localPosition = localPosition;
            var text = textObject.AddComponent<TextMesh>();
            Font font = UiFont();
            text.font = font;
            text.fontSize = FontSize;
            text.fontStyle = FontStyle.Bold;
            // A TextMesh line is about a tenth of fontSize * characterSize units high.
            text.characterSize = height * 10f / FontSize;
            text.anchor = anchor;
            text.alignment = anchor == TextAnchor.MiddleLeft ? TextAlignment.Left : TextAlignment.Center;
            text.color = colour;
            text.text = "";
            var renderer = textObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = font.material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            return text;
        }

        /// <summary>
        /// The UI font. The built-in font has no Cyrillic of its own and borrows it from the system fonts, which a
        /// browser does not offer, so the game carries Inter. The bold face is referenced so that bold text uses it
        /// rather than a smeared copy of the regular one.
        /// </summary>
        static Font UiFont()
        {
            if (AssetImporter.GetAtPath(UiFontPath) is TrueTypeFontImporter importer)
            {
                var bold = AssetDatabase.LoadAssetAtPath<Font>(UiBoldFontPath);
                if (bold != null && (importer.fontReferences == null || importer.fontReferences.Length != 1 || importer.fontReferences[0] != bold))
                {
                    importer.fontReferences = new[] { bold };
                    importer.SaveAndReimport();
                }
            }
            var font = AssetDatabase.LoadAssetAtPath<Font>(UiFontPath);
            if (font == null) throw new System.InvalidOperationException($"UI font missing: {UiFontPath}");
            return font;
        }

        static void Slab(string name, Transform parent, Material material, Vector3 size, Vector3 position)
        {
            GameObject slab = Primitive(PrimitiveType.Cube, name, parent, material);
            slab.transform.localScale = size;
            slab.transform.position = position;
        }

        static void Post(string name, Transform parent, Material material, float x, float height, float radius)
        {
            GameObject post = Primitive(PrimitiveType.Cylinder, name, parent, material);
            post.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            post.transform.position = new Vector3(x, height * 0.5f, 0f);
        }

        /// <summary>
        /// A strip following the net's sagging top edge. With no height given it reaches the ground.
        /// The mesh is generated from the same height function the ball physics collides with.
        /// </summary>
        static void BuildNetRibbon(string name, Transform parent, Material material, float? stripHeight, float uvMetres)
        {
            const int Columns = 48;

            var vertices = new Vector3[(Columns + 1) * 2];
            var uvs = new Vector2[vertices.Length];
            var triangles = new int[Columns * 6];

            for (int column = 0; column <= Columns; column++)
            {
                float x = Mathf.Lerp(-CourtGeometry.NetHalfWidth, CourtGeometry.NetHalfWidth, column / (float)Columns);
                float top = CourtGeometry.NetHeightAt(x);
                float bottom = stripHeight.HasValue ? top - stripHeight.Value : 0f;

                vertices[column * 2] = new Vector3(x, bottom, 0f);
                vertices[column * 2 + 1] = new Vector3(x, top, 0f);

                float scale = 1f / uvMetres;
                uvs[column * 2] = new Vector2(x * scale, bottom * scale);
                uvs[column * 2 + 1] = new Vector2(x * scale, top * scale);
            }

            for (int column = 0; column < Columns; column++)
            {
                int i = column * 6;
                int v = column * 2;
                triangles[i] = v;
                triangles[i + 1] = v + 1;
                triangles[i + 2] = v + 2;
                triangles[i + 3] = v + 2;
                triangles[i + 4] = v + 1;
                triangles[i + 5] = v + 3;
            }

            string path = $"{GeneratedDir}/{name.Replace(" ", string.Empty)}.mesh.asset";
            AssetDatabase.DeleteAsset(path);
            var mesh = new Mesh { name = name, vertices = vertices, uv = uvs, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, path);

            var ribbon = new GameObject(name);
            ribbon.transform.SetParent(parent, false);
            ribbon.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = ribbon.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        static BallController BuildBall(Materials m)
        {
            var root = new GameObject("Ball");
            var controller = root.AddComponent<BallController>();

            // Drawn a little larger than the real 6.7 cm so the ball reads clearly from the camera distance.
            var visual = (GameObject)PrefabUtility.InstantiatePrefab(Load<GameObject>(BallModelPath));
            visual.name = "Visual";
            visual.transform.SetParent(root.transform, false);
            visual.transform.localScale = visual.transform.localScale * BallVisualScale;
            foreach (MeshRenderer renderer in visual.GetComponentsInChildren<MeshRenderer>())
                renderer.sharedMaterial = m.Ball;
            controller.visual = visual.transform;

            controller.shadow = FlatQuad("Ball Shadow", m.BallShadow, 0.3f);
            controller.landingMarker = FlatQuad("Landing Marker", m.LandingMarker, 0.7f);
            controller.landingMarker.gameObject.SetActive(false);
            return controller;
        }

        static Transform FlatQuad(string name, Material material, float size)
        {
            GameObject quad = Primitive(PrimitiveType.Quad, name, null, material);
            quad.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            quad.transform.localScale = Vector3.one * size;
            quad.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            return quad.transform;
        }

        /// <summary>The model sits under a plain root, so replacing it does not disturb the components game code uses.</summary>
        static PlayerController BuildPlayer(string name, Material kit, Materials m, AnimatorController controller, BallController ball)
        {
            var root = new GameObject(name);
            var player = root.AddComponent<PlayerController>();
            var animationController = root.AddComponent<PlayerAnimationController>();

            var model = (GameObject)PrefabUtility.InstantiatePrefab(Load<GameObject>(PlayerModelPath));
            model.name = "Model";
            model.transform.SetParent(root.transform, false);

            Animator animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animationController.animator = animator;

            foreach (SkinnedMeshRenderer skin in model.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                // A racket at full stretch leaves the bind pose's bounds, which would cull it mid-swing.
                skin.updateWhenOffscreen = true;
                skin.sharedMaterial = MaterialForPart(skin.name, kit, m);
                if (skin.name == "Strings") skin.shadowCastingMode = ShadowCastingMode.Off;
            }

            // Fits the clips to the ball after the Animator: racket to the ball, bent knees for low balls, eyes on the ball.
            root.AddComponent<PlayerIkRig>().ball = ball;
            return player;
        }

        static Material MaterialForPart(string partName, Material kit, Materials m)
        {
            switch (partName)
            {
                case "Player": return kit;
                case "Racket": return m.Racket;
                case "Strings": return m.RacketStrings;
                default: throw new System.ArgumentException("Unexpected model part: " + partName);
            }
        }

        static GameObject Primitive(PrimitiveType type, string name, Transform parent, Material material)
        {
            GameObject primitive = GameObject.CreatePrimitive(type);
            primitive.name = name;
            if (parent != null) primitive.transform.SetParent(parent, false);
            primitive.GetComponent<MeshRenderer>().sharedMaterial = material;
            // Ball and player physics are simulated in code: Unity colliders would only cost time.
            Object.DestroyImmediate(primitive.GetComponent<Collider>());
            return primitive;
        }

        static void BuildCamera(PlayerController near, PlayerController far, BallController ball)
        {
            var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = new Color(0.56f, 0.72f, 0.88f);
            camera.farClipPlane = 200f;
            cameraObject.AddComponent<AudioListener>();
            cameraObject.AddComponent<CinemachineBrain>();
            cameraObject.AddComponent<UniversalAdditionalCameraData>();

            var rig = new GameObject("Camera Rig").transform;
            var followTarget = new GameObject("Follow Target").transform;
            followTarget.SetParent(rig, false);
            var lookTarget = new GameObject("Look Target").transform;
            lookTarget.SetParent(rig, false);

            var virtualObject = new GameObject("Gameplay Camera");
            virtualObject.transform.SetParent(rig, false);
            var virtualCamera = virtualObject.AddComponent<CinemachineCamera>();
            virtualCamera.Target.TrackingTarget = followTarget;
            virtualCamera.Target.LookAtTarget = lookTarget;
            virtualCamera.Target.CustomLookAtTarget = true;
            virtualCamera.Lens.NearClipPlane = 0.3f;
            virtualCamera.Lens.FarClipPlane = 200f;

            var follow = virtualObject.AddComponent<CinemachineFollow>();
            follow.FollowOffset = new Vector3(0f, 8f, -11.5f);
            follow.TrackerSettings.BindingMode = Unity.Cinemachine.TargetTracking.BindingMode.WorldSpace;
            follow.TrackerSettings.PositionDamping = new Vector3(0.3f, 0.3f, 0.3f);

            var composer = virtualObject.AddComponent<CinemachineRotationComposer>();
            composer.Damping = new Vector2(0.25f, 0.25f);

            var controller = rig.gameObject.AddComponent<CameraController>();
            controller.virtualCamera = virtualCamera;
            controller.followTarget = followTarget;
            controller.lookTarget = lookTarget;
            controller.trackedPlayer = near;
            controller.otherPlayer = far;
            controller.ball = ball;
        }
    }
}
