using System.IO;
using TennisSim.Simulation;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TennisSim.EditorTools
{
    /// <summary>
    /// Generates the project's assets and the prototype scene from code, so the prototype can be rebuilt
    /// and reviewed as text. Runs once by itself when the project is first opened.
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
        const string ScenePath = SceneDir + "/Prototype.unity";
        const string UrpAssetPath = SettingsDir + "/URP.asset";
        const string UrpRendererPath = SettingsDir + "/URP_Renderer.asset";
        const string BuiltinRendererPath = "Assets/UniversalRenderer.asset";
        const string ControlsPath = SettingsDir + "/TennisControls.inputactions";
        const int InputSystemOnly = 1;

        // Thickness of the painted lines (official values) and their height above the surface.
        const float BaselineWidth = 0.10f;
        const float LineWidth = 0.05f;
        const float LineHeight = 0.004f;
        const float NetTapeHeight = 0.06f;

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
            Materials materials = BuildMaterials();
            BuildScene(materials);

            AssetDatabase.SaveAssets();
            Debug.Log("[Tennis] Project assets and scene are built.");
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

        // ---------------------------------------------------------------- Materials

        sealed class Materials
        {
            public Material Ground, Surround, Court, Line, Post, NetFabric, NetTape;
            public Material Ball, BallShadow, LandingMarker;
            public Material Skin, Racket, Shoes;
            public Material NearShirt, NearShorts, FarShirt, FarShorts;
        }

        static Materials BuildMaterials() => new Materials
        {
            Ground = Lit("Ground", new Color(0.13f, 0.30f, 0.20f), 0.1f),
            Surround = Lit("CourtSurround", new Color(0.16f, 0.42f, 0.34f), 0.25f),
            Court = Lit("CourtSurface", new Color(0.13f, 0.30f, 0.58f), 0.25f),
            Line = Lit("CourtLine", new Color(0.95f, 0.95f, 0.95f), 0.3f),
            Post = Lit("NetPost", new Color(0.12f, 0.14f, 0.13f), 0.5f),
            NetFabric = Transparent("NetFabric", NetTexture(), Color.white, tiling: Vector2.one),
            NetTape = DoubleSided(Lit("NetTape", Color.white, 0.2f)),
            Ball = Lit("Ball", new Color(0.82f, 0.95f, 0.15f), 0.2f),
            BallShadow = Transparent("BallShadow", BlobTexture(), new Color(0f, 0f, 0f, 0.55f), Vector2.one),
            LandingMarker = Transparent("LandingMarker", RingTexture(), new Color(1f, 0.9f, 0.2f, 0.55f), Vector2.one),
            Skin = Lit("Skin", new Color(0.87f, 0.68f, 0.55f), 0.3f),
            Racket = Lit("Racket", new Color(0.15f, 0.15f, 0.18f), 0.6f),
            Shoes = Lit("Shoes", new Color(0.92f, 0.92f, 0.92f), 0.4f),
            NearShirt = Lit("NearShirt", new Color(0.85f, 0.2f, 0.18f), 0.2f),
            NearShorts = Lit("NearShorts", new Color(0.95f, 0.95f, 0.95f), 0.2f),
            FarShirt = Lit("FarShirt", new Color(0.95f, 0.8f, 0.2f), 0.2f),
            FarShorts = Lit("FarShorts", new Color(0.12f, 0.15f, 0.3f), 0.2f),
        };

        static Material Lit(string name, Color color, float smoothness)
        {
            Material material = LoadOrCreate(name, "Universal Render Pipeline/Lit");
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material DoubleSided(Material material)
        {
            material.SetFloat("_Cull", (float)CullMode.Off);
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material Transparent(string name, Texture2D texture, Color color, Vector2 tiling)
        {
            Material material = LoadOrCreate(name, "Universal Render Pipeline/Unlit");
            material.SetTexture("_BaseMap", texture);
            material.SetTextureScale("_BaseMap", tiling);
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

        // ---------------------------------------------------------------- Scene

        static void BuildScene(Materials m)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.62f, 0.65f, 0.7f);
            RenderSettings.skybox = null;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.25f;
            sun.color = new Color(1f, 0.97f, 0.92f);
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(58f, -35f, 0f);

            BuildCourt(m);

            BallController ball = BuildBall(m);
            PlayerController near = BuildPlayer("Player Near", m, m.NearShirt, m.NearShorts);
            PlayerController far = BuildPlayer("Player Far", m, m.FarShirt, m.FarShorts);

            var inputManager = new GameObject("InputManager").AddComponent<InputManager>();
            inputManager.actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(ControlsPath);
            if (inputManager.actions == null) throw new FileNotFoundException("Controls asset not found: " + ControlsPath);

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
            Slab("Surround", court, m.Surround, new Vector3(CourtGeometry.DoublesWidth + 7.3f, 0.002f, CourtGeometry.Length + 12.8f), new Vector3(0f, 0.001f, 0f));
            Slab("Court Surface", court, m.Court, new Vector3(CourtGeometry.DoublesWidth, 0.002f, CourtGeometry.Length), new Vector3(0f, 0.002f, 0f));

            float halfLength = CourtGeometry.HalfLength;
            foreach (float sign in new[] { -1f, 1f })
            {
                // Lines lie inside the court, so their outer edges are the official dimensions.
                Line("Baseline", court, m.Line, CourtGeometry.DoublesWidth, BaselineWidth, 0f, sign * (halfLength - BaselineWidth * 0.5f));
                Line("Service Line", court, m.Line, CourtGeometry.SinglesWidth, LineWidth, 0f, sign * CourtGeometry.ServiceLineFromNet);
                Line("Centre Mark", court, m.Line, LineWidth, 0.10f, 0f, sign * (halfLength - BaselineWidth - 0.05f));
                Line("Singles Sideline", court, m.Line, LineWidth, CourtGeometry.Length, sign * (CourtGeometry.HalfSinglesWidth - LineWidth * 0.5f), 0f);
                Line("Doubles Sideline", court, m.Line, LineWidth, CourtGeometry.Length, sign * (CourtGeometry.HalfDoublesWidth - LineWidth * 0.5f), 0f);

                Post("Net Post", court, m.Post, sign * CourtGeometry.NetHalfWidth, CourtGeometry.NetPostHeight, 0.06f);
                Post("Singles Stick", court, m.Post, sign * CourtGeometry.SinglesStickX, CourtGeometry.NetPostHeight, 0.03f);
            }
            Line("Centre Service Line", court, m.Line, LineWidth, CourtGeometry.ServiceLineFromNet * 2f, 0f, 0f);

            BuildNetRibbon("Net Fabric", court, m.NetFabric, null, 0.1f);
            BuildNetRibbon("Net Tape", court, m.NetTape, NetTapeHeight, 1f);
        }

        static void Slab(string name, Transform parent, Material material, Vector3 size, Vector3 position)
        {
            GameObject slab = Primitive(PrimitiveType.Cube, name, parent, material);
            slab.transform.localScale = size;
            slab.transform.position = position;
        }

        static void Line(string name, Transform parent, Material material, float sizeX, float sizeZ, float x, float z) =>
            Slab(name, parent, material, new Vector3(sizeX, 0.002f, sizeZ), new Vector3(x, LineHeight, z));

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
            GameObject visual = Primitive(PrimitiveType.Sphere, "Visual", root.transform, m.Ball);
            visual.transform.localScale = Vector3.one * 0.095f;
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

        /// <summary>A simple stand-in body. Everything game code touches is on the root, so a real model can replace the children.</summary>
        static PlayerController BuildPlayer(string name, Materials m, Material shirt, Material shorts)
        {
            var root = new GameObject(name);
            var controller = root.AddComponent<PlayerController>();
            var animation = root.AddComponent<PlayerAnimationController>();

            var body = new GameObject("Body").transform;
            body.SetParent(root.transform, false);
            animation.body = body;

            Part(PrimitiveType.Capsule, "Torso", body, shirt, new Vector3(0f, 1.15f, 0f), new Vector3(0.42f, 0.38f, 0.26f));
            Part(PrimitiveType.Capsule, "Hips", body, shorts, new Vector3(0f, 0.82f, 0f), new Vector3(0.38f, 0.17f, 0.25f));
            Part(PrimitiveType.Capsule, "Leg L", body, m.Skin, new Vector3(-0.1f, 0.42f, 0f), new Vector3(0.14f, 0.38f, 0.14f));
            Part(PrimitiveType.Capsule, "Leg R", body, m.Skin, new Vector3(0.1f, 0.42f, 0f), new Vector3(0.14f, 0.38f, 0.14f));
            Part(PrimitiveType.Cube, "Shoe L", body, m.Shoes, new Vector3(-0.1f, 0.05f, 0.05f), new Vector3(0.12f, 0.1f, 0.28f));
            Part(PrimitiveType.Cube, "Shoe R", body, m.Shoes, new Vector3(0.1f, 0.05f, 0.05f), new Vector3(0.12f, 0.1f, 0.28f));
            Part(PrimitiveType.Sphere, "Head", body, m.Skin, new Vector3(0f, 1.66f, 0f), Vector3.one * 0.24f);
            Part(PrimitiveType.Capsule, "Off Arm", body, m.Skin, new Vector3(-0.27f, 1.15f, 0.03f), new Vector3(0.09f, 0.28f, 0.09f));

            // The racket arm extends along +X from the shoulder, which is the axis the animation swings around.
            var arm = new GameObject("Arm Pivot").transform;
            arm.SetParent(body, false);
            arm.localPosition = new Vector3(0.24f, 1.38f, 0f);
            animation.armPivot = arm;

            Part(PrimitiveType.Cube, "Arm", arm, m.Skin, new Vector3(0.26f, 0f, 0f), new Vector3(0.52f, 0.08f, 0.08f));
            Part(PrimitiveType.Cube, "Racket Handle", arm, m.Racket, new Vector3(0.68f, 0f, 0f), new Vector3(0.32f, 0.03f, 0.03f));
            Part(PrimitiveType.Sphere, "Racket Head", arm, m.Racket, new Vector3(1.0f, 0f, 0f), new Vector3(0.34f, 0.27f, 0.025f));

            return controller;
        }

        static void Part(PrimitiveType type, string name, Transform parent, Material material, Vector3 position, Vector3 scale)
        {
            GameObject part = Primitive(type, name, parent, material);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
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
