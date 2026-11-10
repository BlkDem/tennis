using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TennisSim.EditorTools
{
    /// <summary>
    /// The version a build carries: 1.0.N, where N counts the commits up to the one being built, so every release is
    /// later than the last. "-tennisVersion X" on the command line or TENNIS_VERSION in the environment overrides it.
    /// </summary>
    public static class BuildVersion
    {
        const string Fallback = "1.0.0";

        public static string Resolve()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-tennisVersion") return Checked(args[i + 1]);
            string fromEnvironment = Environment.GetEnvironmentVariable("TENNIS_VERSION");
            if (!string.IsNullOrEmpty(fromEnvironment)) return Checked(fromEnvironment);
            string commits = Git("rev-list --count HEAD");
            return int.TryParse(commits, out int count) ? $"1.0.{count}" : Fallback;
        }

        static string Checked(string version)
        {
            if (ReleaseVersion.Parts(version) == null) throw new ArgumentException($"[Tennis] Not a version: {version}");
            return version.Trim();
        }

        static string Git(string arguments)
        {
            try
            {
                var start = new ProcessStartInfo("git", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                };
                using (Process git = Process.Start(start))
                {
                    string output = git.StandardOutput.ReadToEnd().Trim();
                    git.WaitForExit(10000);
                    return git.ExitCode == 0 ? output : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Runs a build with the player's version set to the given one, then puts the project's own back.</summary>
        public static T Stamped<T>(string version, Func<T> build)
        {
            string previous = PlayerSettings.bundleVersion;
            PlayerSettings.bundleVersion = version;
            try
            {
                return build();
            }
            finally
            {
                PlayerSettings.bundleVersion = previous;
            }
        }
    }

    /// <summary>
    /// A Windows release for the game's own updater: the player zipped as TennisSimulator-&lt;version&gt;.zip, with
    /// latest.json beside it giving the version, size and SHA-256. Tools/Release/deploy-windows.sh puts both on the
    /// server, where installed games find them.
    /// </summary>
    public static class ReleaseBuilder
    {
        public const string ReleaseDir = "Build/WindowsRelease";

        /// <summary>Run in batch with: -executeMethod TennisSim.EditorTools.ReleaseBuilder.BuildWindowsRelease</summary>
        [MenuItem("Tennis/Build Windows Release")]
        public static void BuildWindowsRelease()
        {
            string version = BuildVersion.Resolve();
            string player = ProjectBuilder.BuildWindowsPlayer(version);
            Package(player, version, ReleaseDir);
        }

        /// <summary>Zips the player folder and writes latest.json for it into the release folder, emptied first.</summary>
        public static void Package(string playerDir, string version, string releaseDir)
        {
            if (Directory.Exists(releaseDir)) Directory.Delete(releaseDir, true);
            Directory.CreateDirectory(releaseDir);

            string zipName = $"TennisSimulator-{version}.zip";
            string zipPath = Path.Combine(releaseDir, zipName);
            playerDir = Path.GetFullPath(playerDir);
            using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (string file in Directory.GetFiles(playerDir, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(playerDir.Length).TrimStart('\\', '/').Replace('\\', '/');
                    // Unity's debugging leftovers, which its own folder names say not to ship.
                    string top = relative.Split('/')[0];
                    if (top.EndsWith("_DoNotShip") || top.EndsWith("_ButDontShipItWithYourGame")) continue;
                    archive.CreateEntryFromFile(file, relative, System.IO.Compression.CompressionLevel.Optimal);
                }
            }

            var manifest = new UpdateManifest
            {
                version = version,
                file = zipName,
                sha256 = GameUpdater.Sha256(zipPath),
                size = new FileInfo(zipPath).Length,
            };
            File.WriteAllText(Path.Combine(releaseDir, "latest.json"), JsonUtility.ToJson(manifest, true));
            Debug.Log($"[Tennis] Release {version}: {Path.GetFullPath(zipPath)}, {manifest.size / (1024f * 1024f):F1} MB");
        }
    }
}
