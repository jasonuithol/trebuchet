// The host side of valslöngva's externs: every static method here is named by an
// `extern fn` in apps/valslongva/infrastructure/externs.treb. This is the only C# the
// application needs, and it knows nothing about Trebuchet: strings, bools, arrays, tasks.
// Exceptions thrown here become the Failed or Unreachable error variant over there.

using System.Diagnostics;
using System.IO.Compression;

namespace Valslongva;

public static class Host
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("valslongva/0.1 (+https://github.com/jasonuithol/trebuchet)");
        return client;
    }

    public static string Os() => OperatingSystem.IsWindows() ? "windows" : "linux";

    public static string Home() => Slashes(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string DataDir() => Slashes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "valslongva"));

    public static Task<string> ReadText(string path) => File.ReadAllTextAsync(path);

    public static async Task WriteText(string path, string text)
    {
        Parent(path);
        await File.WriteAllTextAsync(path, text);
    }

    public static async Task AppendText(string path, string text)
    {
        Parent(path);
        await File.AppendAllTextAsync(path, text);
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>The file version stamped into an executable or dll, or "" when unknown.</summary>
    public static string FileVersion(string path)
    {
        if (!File.Exists(path)) return "";
        try { return FileVersionInfo.GetVersionInfo(path).FileVersion ?? ""; }
        catch (Exception) { return ""; }
    }

    /// <summary>Every file under a folder, as forward-slash paths relative to it, in a stable order.</summary>
    public static string[] ListFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Slashes(Path.GetRelativePath(dir, f)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    public static void CopyFile(string from, string to)
    {
        Parent(to);
        File.Copy(from, to, overwrite: true);
    }

    public static void DeleteTree(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    public static void MoveTree(string from, string to)
    {
        Parent(to);
        if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to, overwrite: true);
    }

    public static void EnsureDir(string path) => Directory.CreateDirectory(path);

    public static Task<string> HttpGetText(string url) => Http.GetStringAsync(url);

    /// <summary>Downloads to a .part file and renames it, so a half-written archive is never mistaken for a whole one.</summary>
    public static async Task Download(string url, string path)
    {
        Parent(path);
        var part = path + ".part";
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(part);
            await source.CopyToAsync(target);
        }
        File.Move(part, path, overwrite: true);
    }

    /// <summary>Extracts entry by entry: some packages are zipped on Windows with backslashes in entry names.</summary>
    public static Task ExtractZip(string zip, string dir) => Task.Run(() =>
    {
        Directory.CreateDirectory(dir);
        var root = Path.GetFullPath(dir);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/') || name.Length == 0) continue;
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException($"entry escapes the archive: {entry.FullName}");
            Parent(target);
            entry.ExtractToFile(target, overwrite: true);
        }
    });

    /// <summary>Starts a program detached. A shell script goes through sh, since a zip does not carry the executable bit.</summary>
    public static void Run(string program, string workingDir)
    {
        var info = program.EndsWith(".sh", StringComparison.Ordinal)
            ? new ProcessStartInfo("/bin/sh", new[] { program })
            : new ProcessStartInfo(program);
        info.WorkingDirectory = workingDir;
        info.UseShellExecute = false;
        Process.Start(info)?.Dispose();
    }

    public static void OpenUrl(string url)
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        else Process.Start(new ProcessStartInfo("xdg-open", new[] { url }) { UseShellExecute = false })?.Dispose();
    }

    private static void Parent(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    private static string Slashes(string path) => path.Replace('\\', '/');
}
