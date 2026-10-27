using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace MainframeEngine.Symbolicate;

/// <summary>
/// Fills in the files and lines of a crash report's frames from portable PDBs (G15.8): each frame's module MVID picks the
/// assembly in the symbols folder (an assembly is identified by its MVID, so a rebuilt one never matches), its PDB (next
/// to it, or embedded) maps the method token and IL offset to the sequence point at or before the offset.
/// </summary>
public sealed class Symbolicator : IDisposable
{
    private readonly Dictionary<Guid, string> _assemblies = [];
    private readonly Dictionary<Guid, MetadataReaderProvider?> _pdbs = [];

    /// <summary>Indexes the assemblies (<c>*.dll</c>, <c>*.exe</c>) under <paramref name="symbolsFolder"/> by MVID.</summary>
    public Symbolicator(string symbolsFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsFolder);
        foreach (var file in Directory.EnumerateFiles(symbolsFolder, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            if (ReadMvid(file) is { } mvid)
                _assemblies.TryAdd(mvid, file);
        }
    }

    /// <summary>Assemblies indexed.</summary>
    public int AssemblyCount => _assemblies.Count;

    /// <summary>Fills <see cref="CrashFrame.File"/>/<see cref="CrashFrame.Line"/> where a PDB knows them; returns how many frames it resolved.</summary>
    public int Symbolicate(CrashReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var resolved = 0;
        foreach (var exception in report.Exception)
        {
            foreach (var frame in exception.Frames)
            {
                if (Resolve(frame) is { } location)
                {
                    frame.File = location.File;
                    frame.Line = location.Line;
                    resolved++;
                }
            }
        }

        return resolved;
    }

    /// <summary>The file and line of <paramref name="frame"/>, or null when no PDB covers it.</summary>
    public (string File, int Line)? Resolve(CrashFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Mvid is not { } text || !Guid.TryParse(text, out var mvid) || frame.Token is not { } token || frame.IlOffset is not { } offset)
            return null;
        if (PdbOf(mvid) is not { } pdb)
            return null;
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.MethodDefinition)
            return null;
        var reader = pdb.GetMetadataReader();
        var debug = reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(handle)));
        SequencePoint? best = null;
        foreach (var point in debug.GetSequencePoints())
        {
            if (point.IsHidden || point.Offset > offset)
                continue;
            if (best is null || point.Offset >= best.Value.Offset)
                best = point;
        }

        if (best is not { } found)
            return null;
        var document = reader.GetDocument(found.Document);
        return (reader.GetString(document.Name), found.StartLine);
    }

    private MetadataReaderProvider? PdbOf(Guid mvid)
    {
        if (_pdbs.TryGetValue(mvid, out var cached))
            return cached;
        MetadataReaderProvider? provider = null;
        if (_assemblies.TryGetValue(mvid, out var assembly))
        {
            var pdb = Path.ChangeExtension(assembly, ".pdb");
            if (File.Exists(pdb))
            {
                provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdb));
            }
            else
            {
                using var pe = new PEReader(File.OpenRead(assembly));
                foreach (var entry in pe.ReadDebugDirectory())
                {
                    if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                    {
                        provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                        break;
                    }
                }
            }
        }

        _pdbs[mvid] = provider;
        return provider;
    }

    private static Guid? ReadMvid(string path)
    {
        try
        {
            using var pe = new PEReader(File.OpenRead(path));
            if (!pe.HasMetadata)
                return null;
            var reader = pe.GetMetadataReader();
            return reader.GetGuid(reader.GetModuleDefinition().Mvid);
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var provider in _pdbs.Values)
            provider?.Dispose();
        _pdbs.Clear();
    }
}
