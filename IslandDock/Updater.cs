using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IslandDock;

internal sealed record UpdateInfo(Version Version, string Url, string? Sha256);

/// <summary>Compares this copy with the latest GitHub release, and installs that release when asked to.</summary>
internal static class Updater
{
    const string LatestRelease = "https://api.github.com/repos/Thictony/IslandDock/releases/latest";
    const string AssetName = "IslandDock-Setup.exe";
    static readonly HttpClient http = CreateClient();

    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
    /// <summary>The newer release found by the last check, if any.</summary>
    public static UpdateInfo? Available { get; private set; }
    public static bool Busy { get; set; }

    static string DownloadDir => Path.Combine(Path.GetTempPath(), "IslandDock-update");

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("IslandDock");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Returns the latest release if it's newer than this copy, or null if this copy is up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestRelease));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed))
            throw new InvalidDataException($"The latest release has an unexpected version, \"{tag}\".");
        var latest = Normalize(parsed);
        if (latest <= Current) return Available = null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            return Available = new UpdateInfo(latest, asset.GetProperty("browser_download_url").GetString()!,
                digest != null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : null);
        }
        throw new InvalidDataException($"Version {latest} doesn't have {AssetName} attached yet.");
    }

    /// <summary>Downloads the installer and checks it arrived intact, returning its path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update)
    {
        Directory.CreateDirectory(DownloadDir);
        string path = Path.Combine(DownloadDir, AssetName);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using (var response = await http.GetAsync(update.Url, HttpCompletionOption.ResponseHeadersRead, cancel.Token))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file, cancel.Token);
        }
        if (update.Sha256 != null)
        {
            await using var file = File.OpenRead(path);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancel.Token));
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                file.Close();
                File.Delete(path);
                throw new InvalidDataException("The download was damaged. Try again.");
            }
        }
        return path;
    }

    /// <summary>The installer closes the island the normal way (handing docked windows back), updates it and reopens it.</summary>
    public static void RunInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, "/update") { UseShellExecute = true, WorkingDirectory = Path.GetTempPath() });

    /// <summary>Removes the installer left behind by the last update.</summary>
    public static void CleanUp()
    {
        try { if (Directory.Exists(DownloadDir)) Directory.Delete(DownloadDir, true); }
        catch { }
    }

    const uint MB_YESNO = 0x4, MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
    const int IDYES = 6;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    // Docked windows are topmost, so a plain dialog could open behind them.
    public static void Tell(string text) => MessageBoxW(IntPtr.Zero, text, "IslandDock", MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST);
    public static bool Ask(string text) => MessageBoxW(IntPtr.Zero, text, "IslandDock", MB_YESNO | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST) == IDYES;
}
