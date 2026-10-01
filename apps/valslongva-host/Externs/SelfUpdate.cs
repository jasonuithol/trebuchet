// The running program as something that can be replaced. The updater (Trebuchet) decides whether
// a newer release exists and unpacks it; this class swaps the files and restarts the process.
// Only a published single-file build may replace itself: a developer's run from bin/ never does.

using System.Diagnostics;

namespace Valslongva;

public static class SelfUpdate
{
    public const string DefaultFeed = "https://api.github.com/repos/jasonuithol/trebuchet/releases?per_page=30";

    /// <summary>Set by the web host: stops the server so the port is free for the replacement.</summary>
    public static Action? StopServer;

    /// <summary>The arguments the replacement process should be started with (the port, no second browser tab).</summary>
    public static string[] RestartArgs = Array.Empty<string>();

    public static bool RestartRequested { get; private set; }

    private static string ExeName => OperatingSystem.IsWindows() ? "valslongva.exe" : "valslongva";
    private static string? Exe => Environment.ProcessPath;

    /// <summary>A published single-file build has no managed dll beside it; `dotnet run` does.</summary>
    public static bool Updatable() =>
        Exe is { } exe
        && string.Equals(Path.GetFileName(exe), ExeName, StringComparison.OrdinalIgnoreCase)
        && !File.Exists(Path.Combine(AppContext.BaseDirectory, "valslongva.dll"))
        && Environment.GetEnvironmentVariable("VALSLONGVA_NO_UPDATE") is null;

    /// <summary>Where releases are listed. VALSLONGVA_RELEASES_URL overrides it, for testing against a local feed.</summary>
    public static string Feed() => Environment.GetEnvironmentVariable("VALSLONGVA_RELEASES_URL") is { Length: > 0 } url ? url : DefaultFeed;

    /// <summary>Puts the unpacked release in place of the running program: the executable and its ui folder.</summary>
    public static void ReplaceWith(string dir)
    {
        var exe = Exe ?? throw new InvalidOperationException("cannot tell where this program is");
        var home = Path.GetDirectoryName(exe)!;
        var fresh = Path.Combine(dir, ExeName);
        if (!File.Exists(fresh)) throw new FileNotFoundException("the release does not contain " + ExeName, fresh);

        if (OperatingSystem.IsWindows())
        {
            // a running exe cannot be overwritten on Windows, but it can be renamed out of the way
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            File.Copy(fresh, exe);
        }
        else
        {
            // write beside it, then rename over it: the running process keeps its open file
            var staged = exe + ".new";
            File.Copy(fresh, staged, overwrite: true);
            File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(staged, exe, overwrite: true);
        }

        var ui = Path.Combine(dir, "ui");
        if (Directory.Exists(ui))
            foreach (var file in Directory.EnumerateFiles(ui, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(home, "ui", Path.GetRelativePath(ui, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
    }

    /// <summary>Asks the host to stop once the current response has gone out; the host then starts the replacement.</summary>
    public static void Restart()
    {
        RestartRequested = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(600);
            if (StopServer is { } stop) stop();
            else StartReplacement();
        });
    }

    /// <summary>Called by the host after the server has stopped and the port is free.</summary>
    public static void StartReplacement()
    {
        if (Exe is not { } exe) return;
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var a in RestartArgs) info.ArgumentList.Add(a);
        Process.Start(info)?.Dispose();
    }

    /// <summary>Removes what a previous update left behind (the renamed old executable on Windows).</summary>
    public static void CleanUp()
    {
        try { if (Exe is { } exe && File.Exists(exe + ".old")) File.Delete(exe + ".old"); }
        catch (Exception) { /* still locked for a moment after a restart; the next start gets it */ }
    }
}
