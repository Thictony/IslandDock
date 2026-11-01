using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IslandSetup;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool quiet = args.Any(a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase));
        bool install = Setup.HasPayload;
        // The island's update link: keeps the user's shortcut and startup choices, and reopens the island either way.
        if (install && args.Any(a => a.Equals("/update", StringComparison.OrdinalIgnoreCase)))
        {
            string failed = null;
            try { Setup.Install(desktopShortcut: false, _ => { }, update: true); }
            catch (Exception ex)
            {
                failed = ex.Message;
                Setup.ReportToIsland(failed); // it shows this itself, in its update panel
            }
            try { Setup.Launch(); }
            // No island left to tell, which only happens when the install broke partway.
            catch { if (failed != null) MessageBox.Show("IslandDock couldn't be updated.\n\n" + failed + "\n\nYou can download it again from https://github.com/Thictony/IslandDock/releases", "IslandDock"); }
            return;
        }
        if (quiet)
        {
            try
            {
                if (install) Setup.Install(desktopShortcut: false, _ => { });
                else Setup.Uninstall(removeSettings: false, _ => { });
            }
            catch { Environment.Exit(1); }
            return;
        }
        Application.Run(new SetupForm(install));
    }
}

/// <summary>Installs IslandDock for the current user, or removes it. No admin rights needed: everything goes under the
/// user's own profile and registry.</summary>
internal static class Setup
{
    public const string AppName = "IslandDock";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IslandDock";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string UninstallerName = "Uninstall IslandDock.exe";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    static string AppExe => Path.Combine(InstallDir, "IslandDock.exe");
    static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    static string DesktopShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
    static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo("payload.zip") != null;
    public static bool IsInstalled => File.Exists(AppExe);

    public static void Install(bool desktopShortcut, Action<string> status, bool update = false)
    {
        status("Closing IslandDock…");
        CloseRunningIsland();

        status("Copying files…");
        Directory.CreateDirectory(InstallDir);
        // Clear out the old version first, so nothing it had and this one doesn't is left behind.
        foreach (var f in Directory.GetFiles(InstallDir)) TryDelete(f);
        foreach (var d in Directory.GetDirectories(InstallDir)) { try { Directory.Delete(d, true); } catch { } }
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(InstallDir, entry.FullName));
                if (!target.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                entry.ExtractToFile(target, true);
            }
        }

        status("Adding shortcuts…");
        CreateShortcut(StartMenuShortcut, AppExe);
        if (!update)
        {
            if (desktopShortcut) CreateShortcut(DesktopShortcut, AppExe);
            else TryDelete(DesktopShortcut);
        }

        status("Registering…");
        long size = Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            string uninstaller = Path.Combine(InstallDir, UninstallerName);
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
            key.SetValue("Publisher", AppName);
            key.SetValue("DisplayIcon", AppExe);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{uninstaller}\"");
            key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /quiet");
            key.SetValue("EstimatedSize", (int)size, RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        // Starts with Windows by default; the island's own menu turns this off.
        if (!update) using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) run.SetValue(AppName, $"\"{AppExe}\"");
        status("Done");
    }

    public static void Uninstall(bool removeSettings, Action<string> status)
    {
        status("Closing IslandDock…");
        CloseRunningIsland();
        RestoreNotificationBanners();

        status("Removing shortcuts…");
        TryDelete(StartMenuShortcut);
        TryDelete(DesktopShortcut);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) run?.DeleteValue(AppName, false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        if (removeSettings) { try { Directory.Delete(SettingsDir, true); } catch { } }

        status("Removing files…");
        string self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
        if (Directory.Exists(InstallDir))
        {
            foreach (var f in Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories))
                if (!f.Equals(self, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
            foreach (var d in Directory.GetDirectories(InstallDir)) { try { Directory.Delete(d, true); } catch { } }
        }
        // This program is in the folder too, and can't delete itself while running; a hidden command finishes it off.
        if (self.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDir}\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        else { try { Directory.Delete(InstallDir, true); } catch { } }
        status("Done");
    }

    /// <summary>The island puts Windows' notification pop-ups back when it closes; this is for one that was stopped
    /// without closing, which still has them off.</summary>
    static void RestoreNotificationBanners()
    {
        const string apps = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
        const string changed = @"Software\IslandDock\HiddenBanners";
        try
        {
            using (var list = Registry.CurrentUser.OpenSubKey(changed))
            {
                if (list == null) return;
                foreach (var name in list.GetValueNames())
                    using (var app = Registry.CurrentUser.OpenSubKey(apps + "\\" + name, true))
                        if (app?.GetValue("ShowBanner") is int banner && banner == 0) app.DeleteValue("ShowBanner", false);
            }
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\IslandDock", false);
        }
        catch { }
    }

    /// <summary>Leaves word for the island of why its update failed. The same file as <c>Updater.InstallErrorFile</c>.</summary>
    public static void ReportToIsland(string message)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(Path.Combine(SettingsDir, "update-failed.txt"), message);
        }
        catch { }
    }

    public static void Launch() => Process.Start(new ProcessStartInfo(AppExe) { UseShellExecute = true, WorkingDirectory = InstallDir });

    // ------------------------------------------------------------------ closing the running island

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

    /// <summary>
    /// Asks the island to quit the normal way, which hands its docked windows back and brings the taskbar back. It's
    /// never killed: docked windows live inside it and would be lost with it.
    /// </summary>
    static void CloseRunningIsland()
    {
        foreach (var p in Process.GetProcessesByName("IslandDock"))
        {
            uint pid = (uint)p.Id;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint owner);
                var sb = new StringBuilder(64);
                GetWindowText(h, sb, 64);
                if (owner == pid && sb.ToString() == "Island") PostMessage(h, 0x10 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            if (!p.WaitForExit(10000))
                throw new InvalidOperationException("IslandDock is still running. Close it from its menu (Exit Island), then try again.");
        }
    }

    static void CreateShortcut(string path, string target)
    {
        // WScript.Shell through late binding, so there's no interop assembly to carry.
        var type = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(type);
        try
        {
            object link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            var lt = link.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "A dynamic island for Windows" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            Marshal.FinalReleaseComObject(link);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
