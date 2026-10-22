// The one-click installer is the application itself. The published file is a single
// self-contained executable that runs from wherever it was downloaded; "install" copies it and
// its UI into the user's programs folder and adds a Start menu entry (Windows) or a .desktop
// launcher (Linux). Nothing needs administrator rights, and "remove" undoes exactly that,
// leaving settings, the catalogue cache, and the game folder alone.

using System.Diagnostics;

namespace Valslongva;

public static class Setup
{
    public static string AppDir => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "valslongva")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "valslongva", "app");

    private static string ExeName => OperatingSystem.IsWindows() ? "valslongva.exe" : "valslongva";
    private static string InstalledExe => Path.Combine(AppDir, ExeName);

    private static string LauncherPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs", "valslöngva.lnk")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applications", "valslongva.desktop");

    private static string IconPath => Path.Combine(AppDir, "ui", "favicon.svg");

    public static bool IsInstalled => File.Exists(InstalledExe) && File.Exists(LauncherPath);

    /// <summary>True when this process is the installed copy rather than a downloaded one.</summary>
    public static bool RunningInstalled =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(InstalledExe), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static object Status() => new { installed = IsInstalled, path = AppDir, launcher = LauncherPath, runningInstalled = RunningInstalled, portable = Environment.ProcessPath };

    public static string Install()
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("cannot tell where this program is");
        var sourceDir = Path.GetDirectoryName(source)!;
        Directory.CreateDirectory(AppDir);
        if (!RunningInstalled) File.Copy(source, InstalledExe, overwrite: true);
        CopyTree(Path.Combine(sourceDir, "ui"), Path.Combine(AppDir, "ui"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(InstalledExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Directory.CreateDirectory(Path.GetDirectoryName(LauncherPath)!);
        if (OperatingSystem.IsWindows()) { WriteWindowsShortcut(); RegisterScheme(); }
        else WriteDesktopEntry();
        return AppDir;
    }

    public static string Remove()
    {
        if (File.Exists(LauncherPath)) File.Delete(LauncherPath);
        if (OperatingSystem.IsWindows()) UnregisterScheme();
        if (Directory.Exists(AppDir) && !RunningInstalled) Directory.Delete(AppDir, recursive: true);
        return AppDir;
    }

    private static void WriteDesktopEntry()
    {
        var text = $"""
            [Desktop Entry]
            Type=Application
            Name=valslöngva
            Comment=A Valheim mod manager
            Exec={InstalledExe} %u
            Icon={IconPath}
            Terminal=false
            Categories=Game;Utility;
            Keywords=Valheim;mods;BepInEx;Thunderstore;
            MimeType=x-scheme-handler/ror2mm;
            """;
        File.WriteAllText(LauncherPath, text + "\n");
        try { Process.Start(new ProcessStartInfo("update-desktop-database", new[] { Path.GetDirectoryName(LauncherPath)! }) { UseShellExecute = false })?.WaitForExit(5000); }
        catch (Exception) { /* optional: launchers pick the file up regardless */ }
        // Thunderstore's "Install with Mod Manager" button opens ror2mm:// links; be the program that answers them
        try { Process.Start(new ProcessStartInfo("xdg-mime", new[] { "default", Path.GetFileName(LauncherPath), "x-scheme-handler/ror2mm" }) { UseShellExecute = false })?.WaitForExit(5000); }
        catch (Exception) { /* optional: the MimeType line above is enough for most desktops */ }
    }

    /// <summary>Windows learns a URL scheme from the registry: a per-user key, no administrator needed.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterScheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\ror2mm");
        key.SetValue("", "URL:Thunderstore mod install");
        key.SetValue("URL Protocol", "");
        using var icon = key.CreateSubKey("DefaultIcon");
        icon.SetValue("", $"\"{InstalledExe}\",0");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{InstalledExe}\" \"%1\"");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void UnregisterScheme()
    {
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\ror2mm", throwOnMissingSubKey: false); }
        catch (Exception) { /* nothing to undo */ }
    }

    private static void WriteWindowsShortcut()
    {
        // a .lnk is easiest written by the shell itself; PowerShell ships with Windows
        var script = $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{LauncherPath}'); $s.TargetPath = '{InstalledExe}'; $s.WorkingDirectory = '{AppDir}'; $s.Description = 'A Valheim mod manager'; $s.Save()";
        var psi = new ProcessStartInfo("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", script }) { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start PowerShell");
        p.WaitForExit(20000);
        if (!File.Exists(LauncherPath)) throw new InvalidOperationException("the Start menu shortcut was not created");
    }

    private static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
