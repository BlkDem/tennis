using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace TennisSim
{
    /// <summary>What the server says about the latest Windows release: latest.json, next to the release's zip.</summary>
    [Serializable]
    public sealed class UpdateManifest
    {
        public string version;
        /// <summary>The zip's file name, relative to latest.json.</summary>
        public string file;
        /// <summary>The zip's SHA-256, lowercase hex.</summary>
        public string sha256;
        public long size;

        public static UpdateManifest Parse(string json)
        {
            UpdateManifest manifest = JsonUtility.FromJson<UpdateManifest>(json);
            if (manifest == null || ReleaseVersion.Parts(manifest.version) == null || string.IsNullOrEmpty(manifest.file)
                || manifest.file.Contains("/") || manifest.file.Contains("\\") || string.IsNullOrEmpty(manifest.sha256) || manifest.size <= 0)
                throw new FormatException("latest.json is incomplete");
            return manifest;
        }
    }

    /// <summary>Versions are dotted numbers, 1.0.123: the build number grows with every commit.</summary>
    public static class ReleaseVersion
    {
        public static int[] Parts(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            string[] pieces = version.Trim().Split('.');
            var parts = new int[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
                if (!int.TryParse(pieces[i], out parts[i]) || parts[i] < 0) return null;
            return parts;
        }

        /// <summary>True when candidate is a later version than current; a missing part counts as 0.</summary>
        public static bool IsNewer(string candidate, string current)
        {
            int[] a = Parts(candidate), b = Parts(current);
            if (a == null) return false;
            if (b == null) return true;
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }
    }

    public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

    /// <summary>
    /// Keeps the Windows game up to date. It asks the server for latest.json and, if the release there is newer, can
    /// download its zip, check it against the manifest and unpack it beside the game. Installing hands over to a small
    /// PowerShell script that waits for the game to close, copies the new files over it and starts it again: a running
    /// game cannot replace its own files.
    /// </summary>
    public sealed class GameUpdater
    {
        public const string DefaultManifestUrl = "https://tennis.umolab.ru/download/windows/latest.json";
        const int CheckTimeoutSeconds = 15;

        readonly string manifestUrl;
        readonly string currentVersion;
        readonly string installDir;
        readonly string workDir;
        readonly string exeName;

        public UpdateState State { get; private set; } = UpdateState.Idle;
        public UpdateManifest Latest { get; private set; }
        /// <summary>0..1 while downloading.</summary>
        public float Progress { get; private set; }
        public string Error { get; private set; }
        public string CurrentVersion => currentVersion;
        /// <summary>Where the downloaded release is unpacked, ready to be copied over the game.</summary>
        public string StagedDir => Latest == null ? null : Path.Combine(workDir, Latest.version);

        public GameUpdater(string manifestUrl, string currentVersion, string installDir, string workDir, string exeName)
        {
            this.manifestUrl = manifestUrl;
            this.currentVersion = currentVersion;
            this.installDir = installDir;
            this.workDir = workDir;
            this.exeName = exeName;
        }

        /// <summary>
        /// The updater for this running game, or null where there is nothing to update: the editor and the browser.
        /// "-updateUrl &lt;url&gt;" on the command line points it at another latest.json, a test server for one.
        /// </summary>
        public static GameUpdater ForThisGame()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer) return null;
            string url = CommandLineValue("-updateUrl") ?? DefaultManifestUrl;
            string install = Directory.GetParent(Application.dataPath).FullName;
            // The player's data folder is named after its exe: TennisSimulator_Data beside TennisSimulator.exe.
            string data = Path.GetFileName(Application.dataPath);
            string exe = (data.EndsWith("_Data") ? data.Substring(0, data.Length - 5) : Application.productName) + ".exe";
            return new GameUpdater(url, Application.version, install, Path.Combine(Application.temporaryCachePath, "Update"), exe);
        }

        static string CommandLineValue(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        public IEnumerator Check()
        {
            State = UpdateState.Checking;
            using (UnityWebRequest request = UnityWebRequest.Get(manifestUrl))
            {
                request.timeout = CheckTimeoutSeconds;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    // No network is no reason to bother the player: the game simply plays on.
                    Fail("no answer from " + manifestUrl + ": " + request.error, quiet: true);
                    yield break;
                }
                try
                {
                    Latest = UpdateManifest.Parse(request.downloadHandler.text);
                }
                catch (Exception e)
                {
                    Fail(e.Message, quiet: true);
                    yield break;
                }
            }
            State = ReleaseVersion.IsNewer(Latest.version, currentVersion) ? UpdateState.Available : UpdateState.UpToDate;
            Debug.Log($"[Tennis] Version {currentVersion}, latest {Latest.version}: {State}");
        }

        /// <summary>Downloads the release, checks its size and hash, and unpacks it to <see cref="StagedDir"/>.</summary>
        public IEnumerator Download()
        {
            if (Latest == null) yield break;
            if (!CanWrite(installDir))
            {
                Fail($"нет прав на запись в папку игры {installDir}");
                yield break;
            }

            State = UpdateState.Downloading;
            Progress = 0f;
            Directory.CreateDirectory(workDir);
            string zip = Path.Combine(workDir, Latest.file);
            var zipUrl = new Uri(new Uri(manifestUrl), Latest.file);
            using (var request = new UnityWebRequest(zipUrl, UnityWebRequest.kHttpVerbGET))
            {
                request.downloadHandler = new DownloadHandlerFile(zip) { removeFileOnAbort = true };
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    Progress = request.downloadProgress;
                    yield return null;
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Fail("загрузка не удалась: " + request.error);
                    yield break;
                }
            }
            Progress = 1f;

            try
            {
                Unpack(zip);
                State = UpdateState.Ready;
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
            finally
            {
                if (File.Exists(zip)) File.Delete(zip);
            }
        }

        void Unpack(string zip)
        {
            var info = new FileInfo(zip);
            if (info.Length != Latest.size) throw new InvalidDataException($"файл повреждён: {info.Length} байт вместо {Latest.size}");
            if (!string.Equals(Sha256(zip), Latest.sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("файл повреждён: контрольная сумма не совпадает");

            string staged = StagedDir;
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            ZipFile.ExtractToDirectory(zip, staged);
            if (!File.Exists(Path.Combine(staged, exeName)))
                throw new InvalidDataException($"в архиве нет {exeName}");
        }

        public static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".update-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Writes the install script beside the staged release and starts it for the process with the given id; the
        /// caller then quits. Returns false if the script could not be started.
        /// </summary>
        public bool StartInstaller(int processId)
        {
            if (State != UpdateState.Ready) return false;
            try
            {
                string script = WriteInstallScript(workDir);
                string log = Path.Combine(workDir, "install.log");
                var start = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -GamePid {processId} " +
                    $"-Source \"{StagedDir}\" -Target \"{installDir.TrimEnd('\\', '/')}\" -Exe \"{exeName}\" -Log \"{log}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(start);
                Debug.Log($"[Tennis] Installing {Latest.version} from {StagedDir} into {installDir}");
                return true;
            }
            catch (Exception e)
            {
                Fail("не удалось запустить установку: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// The install script: waits for the game to close, copies the new release over it with robocopy (files the
        /// release no longer has are left alone, so nothing else in the folder is ever deleted), then starts the game
        /// again, the old one if the copy failed. Everything it does goes to the log.
        /// </summary>
        public static string WriteInstallScript(string dir)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "install.ps1");
            File.WriteAllText(path, InstallScript);
            return path;
        }

        const string InstallScript = @"param([int]$GamePid, [string]$Source, [string]$Target, [string]$Exe, [string]$Log)
function Note($text) { Add-Content -Path $Log -Value ((Get-Date -Format s) + ' ' + $text) }
try {
    Note ""waiting for the game ($GamePid) to close""
    Wait-Process -Id $GamePid -Timeout 120 -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    robocopy $Source $Target /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw ""robocopy failed with code $LASTEXITCODE"" }
    Note ""copied $Source to $Target""
    Remove-Item -Recurse -Force $Source -ErrorAction SilentlyContinue
} catch {
    Note ""failed: $_""
}
Start-Process -FilePath (Join-Path $Target $Exe) -WorkingDirectory $Target
Note 'started the game'
";

        void Fail(string message, bool quiet = false)
        {
            Error = message;
            State = UpdateState.Failed;
            if (quiet) Debug.Log("[Tennis] Update check: " + message);
            else Debug.LogWarning("[Tennis] Update failed: " + message);
        }

        /// <summary>A failed check that should not be shown: the server could not be reached, or said nothing usable.</summary>
        public bool FailedQuietly => State == UpdateState.Failed && Latest == null;
    }
}
