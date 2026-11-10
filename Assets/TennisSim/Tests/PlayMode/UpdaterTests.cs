using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace TennisSim.Tests
{
    /// <summary>
    /// The Windows updater end to end on local files: a release zipped with its latest.json, read through file:// URLs
    /// as the game reads the server, then the install script copying it over a game folder.
    /// </summary>
    public class UpdaterTests
    {
        const string Exe = "Game.exe";
        string root;

        [SetUp]
        public void MakeFolders()
        {
            root = Path.Combine(Path.GetTempPath(), "TennisUpdaterTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }

        [TearDown]
        public void RemoveFolders()
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }

        /// <summary>A fake game release: an exe and a data folder, zipped, with latest.json; returns the manifest's URL.</summary>
        string PublishRelease(string version, Func<string, string> corruptHash = null)
        {
            string game = Path.Combine(root, "built");
            Directory.CreateDirectory(Path.Combine(game, "Game_Data"));
            File.WriteAllText(Path.Combine(game, Exe), "new " + version);
            File.WriteAllText(Path.Combine(game, "Game_Data", "level.dat"), "data " + version);

            string server = Path.Combine(root, "server");
            Directory.CreateDirectory(server);
            string zipName = $"Game-{version}.zip";
            string zip = Path.Combine(server, zipName);
            ZipFile.CreateFromDirectory(game, zip);
            string hash = GameUpdater.Sha256(zip);
            var manifest = new UpdateManifest { version = version, file = zipName, sha256 = corruptHash?.Invoke(hash) ?? hash, size = new FileInfo(zip).Length };
            string json = Path.Combine(server, "latest.json");
            File.WriteAllText(json, JsonUtility.ToJson(manifest));
            return new Uri(json).AbsoluteUri;
        }

        GameUpdater Updater(string url, string current)
        {
            string install = Path.Combine(root, "install");
            Directory.CreateDirectory(install);
            return new GameUpdater(url, current, install, Path.Combine(root, "work"), Exe);
        }

        [Test]
        public void Versions_CompareByNumberNotByText()
        {
            Assert.That(ReleaseVersion.IsNewer("1.0.10", "1.0.9"), Is.True);
            Assert.That(ReleaseVersion.IsNewer("1.1", "1.0.99"), Is.True);
            Assert.That(ReleaseVersion.IsNewer("1.0.9", "1.0.10"), Is.False);
            Assert.That(ReleaseVersion.IsNewer("1.0.5", "1.0.5"), Is.False);
            Assert.That(ReleaseVersion.IsNewer("1.0.5.0", "1.0.5"), Is.False, "a missing part counts as nought");
            Assert.That(ReleaseVersion.IsNewer("garbage", "1.0.5"), Is.False, "nothing is offered from a bad manifest");
            Assert.That(ReleaseVersion.IsNewer("1.0.5", "1.0"), Is.True);
        }

        [Test]
        public void Manifest_WithoutAHashOrWithAPathIsRefused()
        {
            Assert.Throws<FormatException>(() => UpdateManifest.Parse("{\"version\":\"1.0.2\",\"file\":\"a.zip\",\"size\":10}"));
            Assert.Throws<FormatException>(() => UpdateManifest.Parse("{\"version\":\"1.0.2\",\"file\":\"../a.zip\",\"sha256\":\"ab\",\"size\":10}"));
            Assert.That(UpdateManifest.Parse("{\"version\":\"1.0.2\",\"file\":\"a.zip\",\"sha256\":\"ab\",\"size\":10}").version, Is.EqualTo("1.0.2"));
        }

        [UnityTest]
        public IEnumerator Update_NewerReleaseIsDownloadedCheckedAndUnpacked()
        {
            GameUpdater updater = Updater(PublishRelease("1.0.7"), "1.0.5");
            yield return updater.Check();
            Assert.That(updater.State, Is.EqualTo(UpdateState.Available));

            yield return updater.Download();
            Assert.That(updater.State, Is.EqualTo(UpdateState.Ready), updater.Error);
            Assert.That(File.ReadAllText(Path.Combine(updater.StagedDir, Exe)), Is.EqualTo("new 1.0.7"));
            Assert.That(File.Exists(Path.Combine(updater.StagedDir, "Game_Data", "level.dat")), Is.True);
            Assert.That(Directory.GetFiles(Path.Combine(root, "work"), "*.zip"), Is.Empty, "the download is cleared once unpacked");
        }

        [UnityTest]
        public IEnumerator Update_SameVersionIsNotOffered()
        {
            GameUpdater updater = Updater(PublishRelease("1.0.7"), "1.0.7");
            yield return updater.Check();
            Assert.That(updater.State, Is.EqualTo(UpdateState.UpToDate));
        }

        [UnityTest]
        public IEnumerator Update_DamagedDownloadIsNotUnpacked()
        {
            GameUpdater updater = Updater(PublishRelease("1.0.7", hash => new string('0', hash.Length)), "1.0.5");
            yield return updater.Check();
            yield return updater.Download();
            Assert.That(updater.State, Is.EqualTo(UpdateState.Failed));
            StringAssert.Contains("контрольная сумма", updater.Error);
            Assert.That(Directory.Exists(updater.StagedDir), Is.False);
            Assert.That(updater.StartInstaller(12345), Is.False, "nothing to install");
        }

        [UnityTest]
        public IEnumerator Update_NoServerFailsQuietly()
        {
            GameUpdater updater = Updater(new Uri(Path.Combine(root, "nowhere", "latest.json")).AbsoluteUri, "1.0.5");
            yield return updater.Check();
            Assert.That(updater.FailedQuietly, Is.True, "a game without network just plays on");
        }

        /// <summary>
        /// The script that does the install, run for real: it waits for the game's process to end, copies the release
        /// over the game folder, keeps files of the player's own there, and clears the unpacked release.
        /// </summary>
        [Test]
        public void InstallScript_ReplacesTheGameOnceItHasClosed()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) Assert.Ignore("the install script is PowerShell, for Windows");

            string install = Path.Combine(root, "install");
            Directory.CreateDirectory(Path.Combine(install, "Game_Data"));
            File.WriteAllText(Path.Combine(install, Exe), "old");
            File.WriteAllText(Path.Combine(install, "Game_Data", "level.dat"), "old data");
            File.WriteAllText(Path.Combine(install, "notes.txt"), "the player's own file");
            string staged = Path.Combine(root, "work", "1.0.7");
            Directory.CreateDirectory(Path.Combine(staged, "Game_Data"));
            File.WriteAllText(Path.Combine(staged, Exe), "new");
            File.WriteAllText(Path.Combine(staged, "Game_Data", "level.dat"), "new data");

            // Stands in for the game: the script has to wait for it.
            using (Process game = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 2")
                       { UseShellExecute = false, CreateNoWindow = true }))
            {
                string script = GameUpdater.WriteInstallScript(Path.Combine(root, "work"));
                string log = Path.Combine(root, "work", "install.log");
                var start = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -GamePid {game.Id} -Source \"{staged}\" -Target \"{install}\" -Exe \"{Exe}\" -Log \"{log}\"")
                    { UseShellExecute = false, CreateNoWindow = true };
                var stopwatch = Stopwatch.StartNew();
                using (Process installer = Process.Start(start))
                {
                    Assert.That(installer.WaitForExit(60000), Is.True, "the install script hung");
                }
                Assert.That(game.HasExited, Is.True, "the files were copied while the game still ran");
                Assert.That(stopwatch.Elapsed.TotalSeconds, Is.GreaterThan(1f));
                StringAssert.Contains("copied", File.ReadAllText(log));
            }

            Assert.That(File.ReadAllText(Path.Combine(install, Exe)), Is.EqualTo("new"));
            Assert.That(File.ReadAllText(Path.Combine(install, "Game_Data", "level.dat")), Is.EqualTo("new data"));
            Assert.That(File.Exists(Path.Combine(install, "notes.txt")), Is.True, "nothing else in the folder is touched");
            Assert.That(Directory.Exists(staged), Is.False, "the unpacked release is cleared");
        }
    }
}
