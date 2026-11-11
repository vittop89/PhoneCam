using System.Diagnostics;
using System.Reflection;

namespace PhoneCam;

/// <summary>
/// Finds adb, scrcpy-server and the web page. A development build has them next to the executable;
/// the single-file release carries them as embedded resources and unpacks them on first start,
/// so the downloaded PhoneCam.exe runs on its own.
/// </summary>
static class AppFiles
{
    const string ResourcePrefix = "payload/";
    const string CompleteMarker = ".complete";

    public static string Root { get; private set; } = AppContext.BaseDirectory;

    public static string AdbExe => Path.Combine(Root, "tools", "adb.exe");

    public static string ServerJar => Path.Combine(Root, "tools", "scrcpy-server");

    public static string WebRoot => Path.Combine(Root, "wwwroot");

    /// <summary>Folder of the unpacked WebView2Loader.dll; null when the loader sits next to the executable.</summary>
    public static string? WebView2LoaderFolder { get; private set; }

    // Assembly.Location is empty only inside a single-file bundle, which is exactly when to unpack.
#pragma warning disable IL3000
    static bool IsSingleFile => string.IsNullOrEmpty(typeof(AppFiles).Assembly.Location);
#pragma warning restore IL3000

    public static void Prepare()
    {
        // A development build has everything copied next to it. The release ignores such files on purpose:
        // copied over an older version's folder, it would otherwise run with that version's adb and page.
        if (!IsSingleFile)
            return;

        var assembly = typeof(AppFiles).Assembly;
        string appFolder = Path.Combine(Settings.Folder, "app");
        // One folder per build: a new release never runs with files unpacked by an older one.
        string target = Path.Combine(appFolder, assembly.ManifestModule.ModuleVersionId.ToString("N"));
        if (!File.Exists(Path.Combine(target, CompleteMarker)))
            Unpack(assembly, target);
        Root = target;
        WebView2LoaderFolder = Path.Combine(target, "webview2");

        // Another running copy may still be using the files these clean-ups would delete.
        if (Process.GetProcessesByName("PhoneCam").Length == 1)
        {
            RemoveOtherBuilds(appFolder, target);
            RemoveOldRuntimeExtraction();
        }
    }

    static void Unpack(Assembly assembly, string target)
    {
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("errors.missingPayload");

        // Unpack into a private folder first, so a crash or a second copy never sees half the files.
        string staging = $"{target}.tmp-{Environment.ProcessId}";
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        foreach (string name in names)
        {
            string path = Path.Combine(staging, name[ResourcePrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = assembly.GetManifestResourceStream(name)!;
            using var output = File.Create(path);
            input.CopyTo(output);
        }
        File.WriteAllText(Path.Combine(staging, CompleteMarker), "");

        if (Directory.Exists(target) && !File.Exists(Path.Combine(target, CompleteMarker)))
            Directory.Delete(target, true); // left over by an interrupted start
        try
        {
            Directory.Move(staging, target);
        }
        catch (IOException) when (File.Exists(Path.Combine(target, CompleteMarker)))
        {
            // Another PhoneCam started at the same moment and unpacked the same build.
            Directory.Delete(staging, true);
        }
    }

    static void RemoveOtherBuilds(string appFolder, string current)
    {
        foreach (string folder in Directory.GetDirectories(appFolder))
        {
            if (!string.Equals(folder, current, StringComparison.OrdinalIgnoreCase))
                TryDelete(folder); // an adb server left running by an older build may still hold its files
        }
    }

    // PhoneCam 0.1.0 and 0.1.1 bundled native libraries, which .NET extracts into %TEMP%\.net\PhoneCam
    // (about 8 MB per version) and never removes. This build bundles none, so that folder is only leftovers.
    static void RemoveOldRuntimeExtraction() =>
        TryDelete(Path.Combine(Path.GetTempPath(), ".net", "PhoneCam"));

    static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still in use: try again on a later start.
        }
    }
}
