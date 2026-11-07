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
        const string ScenePath = SceneDir + "/Prototype.unity";
        const string UrpAssetPath = SettingsDir + "/URP.asset";
        const string UrpRendererPath = SettingsDir + "/URP_Renderer.asset";
        const string BuiltinRendererPath = "Assets/UniversalRenderer.asset";
        const string ControlsPath = SettingsDir + "/TennisControls.inputactions";
        const string WindowsPlayerPath = "Build/Windows/TennisSimulator.exe";
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

        const float FenceHeight = 3f;
        const float FenceThickness = 0.2f;

        static readonly string[] LoopingClips =
        {
            "Idle", "Ready", "Walk", "Run", "Sprint", "Backpedal", "StrafeLeft", "StrafeRight",
            "Forehand_Prep", "Backhand_Prep", "Volley_Prep", "Smash_Prep",
        };

        static readonly string[] UpperBodyStates =
        {
            "Forehand_Prep", "Forehand_Swing", "Backhand_Prep", "Backhand_Swing",
            "Volley_Prep", "Volley_Swing", "Smash_Prep", "Smash_Swing", "Recovery",
        };

        static readonly string[] AllClips = LoopingClips
            .Concat(new[] { "Forehand_Swing", "Backhand_Swing", "Volley_Swing", "Smash_Swing", "Serve", "Recovery", "Celebrate", "PointLost" })
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

        static void ConfigureProject()
        {
            PlayerSettings.productName = "Tennis Simulator";
            PlayerSettings.colorSpace = ColorSpace.Linear;

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
            urp.shadowDistance = 70f;
            urp.shadowCascadeCount = 2;
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

            ConfigurePlayerModel();
            ConfigureBallModel();
        }

        static void ConfigureTexture(string path, bool sRgb, bool normalMap, int maxSize, TextureWrapMode wrap = TextureWrapMode.Clamp, int anisotropy = 1, bool keepAlpha = false)
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
            public Material Ground, Court, Fence, Post, NetFabric, NetTape;
            public Material Ball, BallShadow, LandingMarker;
            public Material NearPlayer, FarPlayer, Racket, RacketStrings;
        }

        static Materials BuildMaterials() => new Materials
        {
            Ground = Lit("Ground", new Color(0.13f, 0.30f, 0.20f), 0.1f),
            Court = CourtSurface(),
            Fence = Lit("Fence", new Color(0.035f, 0.11f, 0.075f), 0.15f),
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

            foreach (string name in new[] { "Celebrate", "PointLost", "Serve" })
            {
                AnimatorState state = AddState(body, name, clips);
                AnimatorStateTransition done = state.AddTransition(locomotion);
                done.hasExitTime = true;
                done.exitTime = 0.95f;
                done.duration = 0.2f;
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
            RenderSettings.skybox = null;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.05f;
            sun.color = new Color(1f, 0.97f, 0.92f);
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(58f, -35f, 0f);

            BuildCourt(m);

            BallController ball = BuildBall(m);
            AnimatorController animator = BuildAnimator();
            PlayerController near = BuildPlayer("Player Near", m.NearPlayer, m, animator);
            PlayerController far = BuildPlayer("Player Far", m.FarPlayer, m, animator);

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

            new GameObject("PrototypeOverlay").AddComponent<PrototypeOverlay>().game = game;

            BuildCamera(near, ball);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
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

            Slab("Ground", court, m.Ground, new Vector3(80f, 0.1f, 90f), new Vector3(0f, -0.05f, 0f));
            BuildCourtSurface(court, m.Court);
            BuildFences(court, m.Fence);

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

        /// <summary>Windscreens on three sides. The near end stays open because the camera looks in over it.</summary>
        static void BuildFences(Transform parent, Material material)
        {
            float halfWidth = SurfaceWidth * 0.5f + FenceThickness * 0.5f;
            float halfLength = SurfaceLength * 0.5f + FenceThickness * 0.5f;
            float height = FenceHeight * 0.5f;

            Slab("Fence Far", parent, material, new Vector3(SurfaceWidth + FenceThickness * 2f, FenceHeight, FenceThickness), new Vector3(0f, height, halfLength));
            Slab("Fence Left", parent, material, new Vector3(FenceThickness, FenceHeight, SurfaceLength), new Vector3(-halfWidth, height, 0f));
            Slab("Fence Right", parent, material, new Vector3(FenceThickness, FenceHeight, SurfaceLength), new Vector3(halfWidth, height, 0f));
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
        static PlayerController BuildPlayer(string name, Material kit, Materials m, AnimatorController controller)
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

        static void BuildCamera(PlayerController near, BallController ball)
        {
            var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
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
            controller.ball = ball;
        }
    }
}
