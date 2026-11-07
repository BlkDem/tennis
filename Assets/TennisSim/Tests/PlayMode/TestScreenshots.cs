using System.IO;
using UnityEngine;

namespace TennisSim.Tests
{
    /// <summary>Pictures from the play tests, for judging the result by eye. Saved to Logs/Screenshots.</summary>
    public static class TestScreenshots
    {
        public const string Directory = "Logs/Screenshots";

        /// <param name="withInterface">Also draw the screen-space interface, which a camera render leaves out otherwise.</param>
        public static void Save(Camera camera, string fileName, bool withInterface = false)
        {
            var canvases = withInterface ? Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None) : new Canvas[0];
            foreach (Canvas canvas in canvases)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = camera.nearClipPlane + 0.05f;
            }

            var target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;

            camera.targetTexture = target;
            Canvas.ForceUpdateCanvases();
            camera.Render(); // In batch mode nothing has drawn yet and the first frame comes out blank.
            camera.Render();
            RenderTexture.active = target;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();

            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            foreach (Canvas canvas in canvases) canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(Path.Combine(Directory, fileName), image.EncodeToPNG());
            Object.Destroy(image);
            Object.Destroy(target);
        }
    }
}
