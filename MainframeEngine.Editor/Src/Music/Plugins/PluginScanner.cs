using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine.Editor.Music;

/// <summary>A scanned VST3 class.</summary>
public sealed record PluginInfo(string ClassId, string Name, string Vendor, string Version, string SubCategories, bool IsInstrument, string BundlePath)
{
    /// <summary>The song's descriptor for this class.</summary>
    public PluginDescriptor ToDescriptor() => new() { Format = "vst3", ClassId = ClassId, Name = Name, Vendor = Vendor };

    public string DisplayName => string.IsNullOrEmpty(Vendor) ? Name : $"{Name} ({Vendor})";
}

/// <summary>The VST3 classes found by the last scan (<see cref="PluginScanner"/>), by class ID.</summary>
public sealed class PluginCatalog
{
    private static PluginCatalog? s_current;
    private readonly Dictionary<string, PluginInfo> _byId;

    public PluginCatalog(IEnumerable<PluginInfo> plugins, IEnumerable<(string Bundle, string Error)>? failures = null)
    {
        Plugins = [.. plugins.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
        Failures = [.. failures ?? []];
        _byId = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Plugins)
            _byId.TryAdd(p.ClassId, p);
    }

    public static PluginCatalog Empty { get; } = new([]);

    /// <summary>The editor's catalog: the cache (<c>~/.mainframe/plugins.json</c>) until a scan replaces it.</summary>
    public static PluginCatalog Current
    {
        get => Volatile.Read(ref s_current) ?? (s_current = PluginScanner.LoadCache(PluginScanner.DefaultCachePath));
        set => Volatile.Write(ref s_current, value);
    }

    public IReadOnlyList<PluginInfo> Plugins { get; }

    public IReadOnlyList<(string Bundle, string Error)> Failures { get; }

    public IEnumerable<PluginInfo> Instruments => Plugins.Where(p => p.IsInstrument);

    public IEnumerable<PluginInfo> Effects => Plugins.Where(p => !p.IsInstrument);

    public PluginInfo? Find(string? classId) => classId is not null && _byId.TryGetValue(classId, out var info) ? info : null;

    /// <summary>The bundle that holds a class in <see cref="Current"/> (the plugin rack's resolver).</summary>
    public static string? Resolve(string classId) => Current.Find(classId)?.BundlePath;
}

/// <summary>
/// Finds VST3 bundles in the default folders of the OS plus the folders from Editor Settings and asks the helper about
/// each in its own process (<c>mfplughost --scan</c>, with a timeout, so a plugin that hangs or crashes while loading
/// only fails its own bundle). Results are cached in <c>~/.mainframe/plugins.json</c> keyed by path and modification
/// time, failures too (a broken bundle is not retried until it changes).
/// </summary>
public sealed class PluginScanner(string helperPath, string cachePath)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static int s_scanning;

    public string HelperPath { get; } = helperPath;

    public string CachePath { get; } = cachePath;

    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    public static string DefaultCachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mainframe", "plugins.json");

    /// <summary>Where hosts look for VST3 plugins on this OS (Steinberg's documented locations).</summary>
    public static IReadOnlyList<string> DefaultFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return ["/Library/Audio/Plug-Ins/VST3", Path.Combine(home, "Library/Audio/Plug-Ins/VST3")];
        if (OperatingSystem.IsWindows())
        {
            return
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common", "VST3"),
            ];
        }

        return [Path.Combine(home, ".vst3"), "/usr/lib/vst3", "/usr/local/lib/vst3"];
    }

    /// <summary>Every <c>.vst3</c> bundle (directory or single file) under <paramref name="folder"/>.</summary>
    public static IEnumerable<string> FindBundles(string folder)
    {
        if (!Directory.Exists(folder))
            yield break;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out var dir))
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
                    yield return entry;
                else if (Directory.Exists(entry))
                    pending.Push(entry);
            }
        }
    }

    /// <summary>Scans the folders (cached bundles are reused unless <paramref name="force"/>) and writes the cache.</summary>
    public PluginCatalog Scan(IEnumerable<string> folders, bool force = false, CancellationToken cancellation = default)
    {
        var cache = force ? new Dictionary<string, PluginCacheBundle>() : ReadCache(CachePath);
        var result = new List<PluginCacheBundle>();
        foreach (var bundle in folders.Distinct(StringComparer.Ordinal).SelectMany(FindBundles).Distinct(StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            var mtime = ModifiedTicks(bundle);
            if (cache.TryGetValue(bundle, out var cached) && cached.Mtime == mtime)
            {
                result.Add(cached);
                continue;
            }

            var scanned = ScanBundle(bundle);
            scanned.Mtime = mtime;
            if (scanned.Error is not null)
                Log.Warning($"[Music] Plugin scan: {Path.GetFileName(bundle)}: {scanned.Error}");
            result.Add(scanned);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            AtomicFile.WriteAllBytes(CachePath, JsonSerializer.SerializeToUtf8Bytes(new PluginCacheData { Bundles = result }, PluginCacheJson.Default.PluginCacheData));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Music] Plugin cache '{CachePath}' could not be written: {e.Message}");
        }

        return ToCatalog(result);
    }

    /// <summary>One bundle through <c>mfplughost --scan</c> (its own process).</summary>
    public PluginCacheBundle ScanBundle(string bundle)
    {
        var start = new ProcessStartInfo(HelperPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--scan");
        start.ArgumentList.Add(bundle);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("no process");
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return new PluginCacheBundle { Path = bundle, Error = $"scan timed out after {Timeout.TotalSeconds:0} s" };
            }

            var json = output.Result.Trim();
            var line = json.LastIndexOf('\n') is var nl and >= 0 ? json[(nl + 1)..] : json;
            var parsed = string.IsNullOrEmpty(line) ? null : JsonSerializer.Deserialize(line, PluginCacheJson.Default.PluginCacheBundle);
            if (parsed is null)
                return new PluginCacheBundle { Path = bundle, Error = $"scan failed (exit code {process.ExitCode})" };
            parsed.Path = bundle;
            if (process.ExitCode != 0 && parsed.Error is null)
                parsed.Error = $"scan failed (exit code {process.ExitCode})";
            return parsed;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or JsonException or IOException)
        {
            return new PluginCacheBundle { Path = bundle, Error = "scan failed: " + e.Message };
        }
    }

    /// <summary>The catalog in a cache file (empty when there is none).</summary>
    public static PluginCatalog LoadCache(string path) => ToCatalog(ReadCache(path).Values);

    /// <summary>Editor Settings › Rescan plugins (and the first song tab with no cache): scans in the background.</summary>
    public static Task<PluginCatalog>? RescanInBackground(IEnumerable<string> extraFolders, bool force)
    {
        if (PluginHostClient.Locate() is not { } helper || Interlocked.Exchange(ref s_scanning, 1) == 1)
            return null;
        var folders = DefaultFolders().Concat(extraFolders.Where(f => !string.IsNullOrWhiteSpace(f))).ToList();
        return Task.Run(() =>
        {
            try
            {
                Log.Info("[Music] Scanning VST3 plugins…");
                var catalog = new PluginScanner(helper, DefaultCachePath).Scan(folders, force);
                PluginCatalog.Current = catalog;
                Log.Info($"[Music] Found {catalog.Plugins.Count} VST3 plugin(s){(catalog.Failures.Count > 0 ? $"; {catalog.Failures.Count} bundle(s) failed" : "")}.");
                return catalog;
            }
            finally
            {
                Volatile.Write(ref s_scanning, 0);
            }
        });
    }

    private static Dictionary<string, PluginCacheBundle> ReadCache(string path)
    {
        var result = new Dictionary<string, PluginCacheBundle>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;
        try
        {
            if (JsonSerializer.Deserialize(File.ReadAllBytes(path), PluginCacheJson.Default.PluginCacheData) is { } data)
            {
                foreach (var b in data.Bundles)
                    result[b.Path] = b;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Music] Plugin cache '{path}' could not be read ({e.Message}); rescanning.");
        }

        return result;
    }

    private static PluginCatalog ToCatalog(IEnumerable<PluginCacheBundle> bundles)
    {
        var list = bundles.ToList();
        var plugins = list.SelectMany(b => b.Classes.Where(c => !string.IsNullOrEmpty(c.ClassId)).Select(c =>
            new PluginInfo(c.ClassId, c.Name ?? c.ClassId, c.Vendor ?? "", c.Version ?? "", c.SubCategories ?? "",
                string.Equals(c.Kind, "instrument", StringComparison.Ordinal), b.Path)));
        return new PluginCatalog(plugins, list.Where(b => b.Error is not null).Select(b => (b.Path, b.Error!)));
    }

    private static long ModifiedTicks(string path) =>
        Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path).Ticks : File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
}

public sealed class PluginCacheData
{
    public int Format { get; set; } = 1;

    public List<PluginCacheBundle> Bundles { get; set; } = [];
}

/// <summary>One bundle in the cache (also the shape <c>mfplughost --scan</c> prints).</summary>
public sealed class PluginCacheBundle
{
    [JsonPropertyName("bundle")]
    public string Path { get; set; } = "";

    public long Mtime { get; set; }

    public List<PluginCacheClass> Classes { get; set; } = [];

    public string? Error { get; set; }
}

public sealed class PluginCacheClass
{
    public string ClassId { get; set; } = "";
    public string? Name { get; set; }
    public string? Vendor { get; set; }
    public string? Version { get; set; }
    public string? SdkVersion { get; set; }
    public string? SubCategories { get; set; }
    public string? Kind { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, NewLine = "\n", PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PluginCacheData))]
internal sealed partial class PluginCacheJson : JsonSerializerContext;
