using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace AlienShooterLauncher;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Title, string DownloadUrl, string Sha256);

/// <summary>
/// Self-update from the latest GitHub release: download the zip, check it against the SHA-256
/// that GitHub publishes for the asset, unpack it aside and only then replace the launcher.
/// </summary>
public static class Updater
{
    public const string Repo = "Cris9870/alien-shooter-widescreen-fix";
    public const string AssetName = "AlienShooterWidescreenFix-windows.zip";
    public const string LauncherName = "Alien Shooter Launcher.exe";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public static string ExePath => Assembly.GetExecutingAssembly().Location;
    static string OldExePath => ExePath + ".old";

    /// <summary>Returns the latest release if it is newer than this launcher; null otherwise.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var client = NewClient(TimeSpan.FromSeconds(10));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        string json = await client.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");

        var release = ParseRelease(json);
        var asset = release?.Assets?.FirstOrDefault(a => a.Name == AssetName);
        if (release?.Tag == null || asset?.Url == null) return null;
        // Without the fingerprint the download cannot be verified, so it is not offered.
        if (asset.Digest == null || !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Version.TryParse(release.Tag.TrimStart('v', 'V'), out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        if (version <= Current) return null;

        return new UpdateInfo(version, release.Name ?? release.Tag, asset.Url, asset.Digest.Substring(7).ToLowerInvariant());
    }

    /// <summary>Downloads, verifies and installs the update next to the running launcher.</summary>
    public static async Task ApplyAsync(UpdateInfo update, IProgress<string> progress)
    {
        string temp = Path.Combine(Path.GetTempPath(), "AlienShooterUpdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string zip = Path.Combine(temp, AssetName);
            progress.Report("Descargando...");
            using (var client = NewClient(TimeSpan.FromMinutes(5)))
            using (var source = await client.GetStreamAsync(update.DownloadUrl))
            using (var file = File.Create(zip))
            {
                await source.CopyToAsync(file);
            }

            progress.Report("Comprobando...");
            if (Sha256(zip) != update.Sha256)
                throw new InvalidOperationException("la descarga no coincide con la huella publicada en GitHub");

            string unpacked = Path.Combine(temp, "files");
            ZipFile.ExtractToDirectory(zip, unpacked);
            if (!File.Exists(Path.Combine(unpacked, LauncherName)))
                throw new InvalidOperationException("el paquete no contiene el lanzador");

            progress.Report("Instalando...");
            Install(unpacked);
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>Starts the updated launcher; the caller then closes this one.</summary>
    public static void Restart() =>
        Process.Start(new ProcessStartInfo(ExePath, "--updated") { UseShellExecute = false });

    /// <summary>Removes the previous launcher left behind by an update (it may still be closing).</summary>
    public static async Task CleanupAsync()
    {
        for (int i = 0; i < 20 && File.Exists(OldExePath); i++)
        {
            try
            {
                File.Delete(OldExePath);
            }
            catch (Exception)
            {
                await Task.Delay(500);
            }
        }
    }

    // A running exe cannot be overwritten but it can be renamed: move it aside, put the new one
    // in its place, and restore the old one if that fails.
    static void Install(string unpacked)
    {
        string exe = ExePath;
        string dir = Path.GetDirectoryName(exe)!;
        if (File.Exists(OldExePath)) File.Delete(OldExePath);
        File.Move(exe, OldExePath);
        try
        {
            File.Copy(Path.Combine(unpacked, LauncherName), exe);
        }
        catch
        {
            File.Move(OldExePath, exe);
            throw;
        }

        // The rest of the package (readme, licence, manual-install files) is best effort.
        foreach (string file in Directory.GetFiles(unpacked, "*", SearchOption.AllDirectories))
        {
            string relative = file.Substring(unpacked.Length).TrimStart(Path.DirectorySeparatorChar);
            if (relative.Equals(LauncherName, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                string target = Path.Combine(dir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
            catch (Exception)
            {
                // A locked or read-only extra file does not stop the update.
            }
        }
    }

    static HttpClient NewClient(TimeSpan timeout)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AlienShooterLauncher/{Current}");
        return client;
    }

    static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var file = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
    }

    static void TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (Exception)
        {
            // Leftovers in %TEMP% are harmless.
        }
    }

    static GitHubRelease? ParseRelease(string json)
    {
        var serializer = new DataContractJsonSerializer(typeof(GitHubRelease));
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return serializer.ReadObject(ms) as GitHubRelease;
    }

    [DataContract]
    sealed class GitHubRelease
    {
        [DataMember(Name = "tag_name")] public string? Tag { get; set; }
        [DataMember(Name = "name")] public string? Name { get; set; }
        [DataMember(Name = "assets")] public GitHubAsset[]? Assets { get; set; }
    }

    [DataContract]
    sealed class GitHubAsset
    {
        [DataMember(Name = "name")] public string? Name { get; set; }
        [DataMember(Name = "browser_download_url")] public string? Url { get; set; }
        [DataMember(Name = "digest")] public string? Digest { get; set; }
    }
}
