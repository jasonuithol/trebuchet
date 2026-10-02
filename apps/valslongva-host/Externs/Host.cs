// The host side of valslöngva's externs: every static method here is named by an
// `extern fn` in apps/valslongva/infrastructure/externs.treb. This is the only C# the
// application needs, and it knows nothing about Trebuchet: strings, bools, arrays, tasks.
// Exceptions thrown here become the Failed or Unreachable error variant over there.

using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

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

    /// <summary>
    /// "guid|name|version" from a plugin assembly's [BepInPlugin] attribute, read from the metadata
    /// without loading the assembly; "" when the file is not a plugin or cannot be read.
    /// </summary>
    public static string PluginInfo(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file);
            if (!pe.HasMetadata) return "";
            var md = pe.GetMetadataReader();
            foreach (var handle in md.CustomAttributes)
            {
                var attribute = md.GetCustomAttribute(handle);
                if (attribute.Parent.Kind != HandleKind.TypeDefinition) continue;
                if (AttributeTypeName(md, attribute) != "BepInPlugin") continue;
                var value = attribute.DecodeValue(new NameOnlyTypes());
                var args = value.FixedArguments.Select(a => a.Value?.ToString() ?? "").ToArray();
                if (args.Length >= 3) return string.Join("|", args.Take(3));
            }
        }
        catch (Exception) { /* not a managed assembly, or unreadable: not a plugin then */ }
        return "";
    }

    private static string AttributeTypeName(MetadataReader md, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
            {
                var parent = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                return parent.Kind == HandleKind.TypeReference ? md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name) : "";
            }
            case HandleKind.MethodDefinition:
            {
                var type = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
                return md.GetString(md.GetTypeDefinition(type).Name);
            }
            default: return "";
        }
    }

    /// <summary>The attribute decoder wants a type model; names are all it needs here.</summary>
    private sealed class NameOnlyTypes : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
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

    private static readonly HttpClient NoRedirects = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Where an address redirects to, without going there. Throws when it does not redirect.</summary>
    public static async Task<string> HttpRedirect(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("valslongva/0.2 (+https://github.com/jasonuithol/trebuchet)");
        using var response = await NoRedirects.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var code = (int)response.StatusCode;
        if (code is < 300 or >= 400 || response.Headers.Location is not { } location)
            throw new HttpRequestException($"{url} did not redirect (HTTP {code})");
        return (location.IsAbsoluteUri ? location : new Uri(new Uri(url), location)).ToString();
    }

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

    /// <summary>
    /// Unpacks an archive. Zips go entry by entry, since some packages are zipped on Windows with
    /// backslashes in entry names; a .tar.gz (the program's own Linux release) keeps its file modes.
    /// </summary>
    public static Task ExtractZip(string zip, string dir) => Task.Run(() =>
    {
        Directory.CreateDirectory(dir);
        if (zip.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || zip.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(zip);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            System.Formats.Tar.TarFile.ExtractToDirectory(gz, dir, overwriteFiles: true);
            return;
        }
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

    /// <summary>Whether a Steam client process is up. The name is "steam" on Linux and Windows alike.</summary>
    public static bool SteamRunning()
    {
        try { return Process.GetProcessesByName("steam").Length > 0; }
        catch (Exception) { return false; }
    }

    /// <summary>The last part of a text file, for logs that grow.</summary>
    public static async Task<string> TailText(string path, long bytes)
    {
        await using var f = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (f.Length > bytes) f.Seek(-bytes, SeekOrigin.End);
        using var reader = new StreamReader(f);
        return await reader.ReadToEndAsync();
    }

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
