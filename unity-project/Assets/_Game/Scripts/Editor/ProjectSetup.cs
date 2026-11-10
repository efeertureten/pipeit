using UnityEditor;
using UnityEngine;
#if SU_URP
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#endif

namespace SuUstasi.EditorTools
{
    /// <summary>Imports everything under Assets/_Game/**/Sprites as transparent 2D sprites (sliced PNGs + reference JPGs).</summary>
    public sealed class SpriteImportPostprocessor : AssetPostprocessor
    {
        private void OnPreprocessTexture()
        {
            var path = assetPath.Replace('\\', '/');
            if (!path.StartsWith("Assets/_Game/") || !path.Contains("/Sprites/")) return;
            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.spritePixelsPerUnit = 100;
            importer.maxTextureSize = path.Contains("/References/") ? 2048 : 1024;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
        }
    }

    public static class ProjectSetup
    {
        private const string MainScene = "Assets/_Game/Scenes/Main.unity";

        /// <summary>Creates an empty Main scene and registers it in Build Settings (GameBootstrap fills it at runtime).</summary>
        [MenuItem("Su Ustası/Setup/Create Main Scene", priority = 19)]
        public static void CreateMainScene()
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Game/Scenes")) AssetDatabase.CreateFolder("Assets/_Game", "Scenes");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(MainScene) == null)
            {
                var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
                UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, MainScene);
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(MainScene, true) };
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(MainScene);
            Debug.Log("[SuUstasi] " + MainScene + " created and added to Build Settings — press Play.");
        }

        [MenuItem("Su Ustası/Setup/Reimport Sprites", priority = 20)]
        public static void ReimportSprites()
        {
            AssetDatabase.ImportAsset("Assets/_Game/Resources/Sprites", ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset("Assets/_Game/Sprites", ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
            Debug.Log("[SuUstasi] sprites reimported");
        }

#if SU_URP
        /// <summary>Creates a URP asset with the 2D Renderer and assigns it in Graphics + Quality settings.</summary>
        [MenuItem("Su Ustası/Setup/Create URP 2D Pipeline Asset", priority = 21)]
        public static void CreateUrp2D()
        {
            const string folder = "Assets/_Game/Settings";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/_Game", "Settings");
            var rendererData = ScriptableObject.CreateInstance<Renderer2DData>();
            AssetDatabase.CreateAsset(rendererData, folder + "/SuUstasi_Renderer2D.asset");
            var pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            AssetDatabase.CreateAsset(pipeline, folder + "/SuUstasi_URP2D.asset");
            AssetDatabase.SaveAssets();
#if UNITY_6000_0_OR_NEWER
            GraphicsSettings.defaultRenderPipeline = pipeline;
#else
            GraphicsSettings.renderPipelineAsset = pipeline;
#endif
            QualitySettings.renderPipeline = pipeline;
            Debug.Log("[SuUstasi] URP 2D pipeline assigned: " + folder + "/SuUstasi_URP2D.asset");
        }
#endif
    }
}
