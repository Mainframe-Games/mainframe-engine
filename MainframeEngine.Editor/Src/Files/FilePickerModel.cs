namespace MainframeEngine.Editor;

/// <summary>What the file picker is for.</summary>
public enum FilePickerMode
{
    /// <summary>Choose an existing file matching the filter.</summary>
    Open,

    /// <summary>Choose a file name to write (may not exist yet; the default extension is appended).</summary>
    Save,

    /// <summary>Choose a folder.</summary>
    Folder,
}

/// <summary>A file or folder in the listing.</summary>
public sealed record FileEntry(string Name, string FullPath, bool IsDirectory, long Size);

/// <summary>
/// The RmlUi file picker's model (native dialogs are not available through SDL2): a folder listing (folders first, then
/// files matching the filter, hidden entries skipped), navigation, the file name field and result validation for
/// open/save/folder. Pure file-system logic, so it is unit-tested; <see cref="FilePickerDialog"/> is the view.
/// </summary>
public sealed class FilePickerModel
{
    private readonly List<FileEntry> _entries = [];
    private IReadOnlyList<string> _filter = [];

    public FilePickerModel(FilePickerMode mode, string startDirectory, IReadOnlyList<string>? filter = null, string? fileName = null)
    {
        Mode = mode;
        Filter = filter ?? [];
        FileName = fileName ?? "";
        if (!Navigate(startDirectory))
            Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public FilePickerMode Mode { get; }

    /// <summary>Glob patterns (<c>*.mscene</c>); empty lists every file. The first pattern's extension is the save default.</summary>
    public IReadOnlyList<string> Filter
    {
        get => _filter;
        set
        {
            _filter = value ?? [];
            if (CurrentDirectory is not null)
                Refresh();
        }
    }

    /// <summary>Lists hidden entries (dot files) too.</summary>
    public bool ShowHidden { get; set; }

    /// <summary>The folder being listed (absolute).</summary>
    public string CurrentDirectory { get; private set; } = null!;

    public IReadOnlyList<FileEntry> Entries => _entries;

    /// <summary>The file name field (a name in <see cref="CurrentDirectory"/>, or an absolute path).</summary>
    public string FileName { get; set; }

    /// <summary>The last navigation or validation error, for the dialog's message line.</summary>
    public string? Error { get; private set; }

    /// <summary>The default extension for <see cref="FilePickerMode.Save"/> (from the first filter pattern), e.g. <c>.mscene</c>.</summary>
    public string? DefaultExtension
    {
        get
        {
            foreach (var pattern in _filter)
            {
                var extension = Path.GetExtension(pattern);
                if (extension.Length > 1 && !extension.Contains('*', StringComparison.Ordinal))
                    return extension;
            }

            return null;
        }
    }

    /// <summary>Lists <paramref name="directory"/>; false (and <see cref="Error"/>) when it cannot be read.</summary>
    public bool Navigate(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return false;
        string full;
        try
        {
            full = Path.GetFullPath(directory);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Error = e.Message;
            return false;
        }

        if (!Directory.Exists(full))
        {
            Error = $"Folder not found: {full}";
            return false;
        }

        var previous = CurrentDirectory;
        CurrentDirectory = full;
        if (Refresh())
            return true;
        if (previous is not null)
        {
            CurrentDirectory = previous;
            Refresh();
        }

        return false;
    }

    /// <summary>Goes to the parent folder; false at the file-system root.</summary>
    public bool Up()
    {
        var parent = Directory.GetParent(CurrentDirectory);
        return parent is not null && Navigate(parent.FullName);
    }

    /// <summary>Re-reads the current folder.</summary>
    public bool Refresh()
    {
        _entries.Clear();
        try
        {
            var directory = new DirectoryInfo(CurrentDirectory);
            var folders = new List<FileEntry>();
            var files = new List<FileEntry>();
            foreach (var info in directory.EnumerateFileSystemInfos())
            {
                if (!ShowHidden && (info.Name.StartsWith('.') || (info.Attributes & FileAttributes.Hidden) != 0))
                    continue;
                if (info is DirectoryInfo)
                    folders.Add(new FileEntry(info.Name, info.FullName, true, 0));
                else if (Mode != FilePickerMode.Folder && Matches(info.Name))
                    files.Add(new FileEntry(info.Name, info.FullName, false, ((FileInfo)info).Length));
            }

            folders.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            files.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            _entries.AddRange(folders);
            _entries.AddRange(files);
            Error = null;
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Error = $"Cannot read {CurrentDirectory}: {e.Message}";
            return false;
        }
    }

    /// <summary>Whether a file name passes the filter (case-insensitive globs with <c>*</c> and <c>?</c>).</summary>
    public bool Matches(string fileName)
    {
        if (_filter.Count == 0)
            return true;
        foreach (var pattern in _filter)
            if (Glob(fileName, pattern))
                return true;
        return false;
    }

    /// <summary>A click on an entry: folders open, files fill the name field.</summary>
    public void Select(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory)
        {
            if (Mode == FilePickerMode.Folder)
                FileName = entry.Name;
            return;
        }

        FileName = entry.Name;
    }

    /// <summary>A double click: folders are entered, files accepted (the caller then calls <see cref="TryGetResult"/>).</summary>
    public bool Activate(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory)
        {
            Navigate(entry.FullPath);
            if (Mode == FilePickerMode.Folder)
                FileName = "";
            return false;
        }

        FileName = entry.Name;
        return true;
    }

    /// <summary>
    /// The chosen path: an existing matching file (open), a writable target with the default extension (save; check
    /// <paramref name="exists"/> to confirm overwriting) or a folder. False with <see cref="Error"/> when invalid.
    /// </summary>
    public bool TryGetResult(out string path, out bool exists)
    {
        path = "";
        exists = false;
        var name = FileName.Trim();

        if (Mode == FilePickerMode.Folder)
        {
            path = name.Length == 0 ? CurrentDirectory : Path.GetFullPath(Path.Combine(CurrentDirectory, name));
            if (!Directory.Exists(path))
            {
                Error = $"Folder not found: {path}";
                return false;
            }

            exists = true;
            return true;
        }

        if (name.Length == 0)
        {
            Error = "Enter a file name.";
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            Error = $"'{name}' is not a valid file name.";
            return false;
        }

        var full = Path.GetFullPath(Path.Combine(CurrentDirectory, name));
        if (Directory.Exists(full))
        {
            Error = $"'{name}' is a folder.";
            return false;
        }

        if (Mode == FilePickerMode.Save)
        {
            var extension = DefaultExtension;
            if (extension is not null && !Matches(Path.GetFileName(full)))
                full += extension;
            if (!Directory.Exists(Path.GetDirectoryName(full)))
            {
                Error = $"Folder not found: {Path.GetDirectoryName(full)}";
                return false;
            }

            path = full;
            exists = File.Exists(full);
            Error = null;
            return true;
        }

        if (!File.Exists(full))
        {
            Error = $"File not found: {full}";
            return false;
        }

        if (!Matches(Path.GetFileName(full)))
        {
            Error = $"'{Path.GetFileName(full)}' is not a {string.Join(" or ", _filter)} file.";
            return false;
        }

        path = full;
        exists = true;
        Error = null;
        return true;
    }

    /// <summary>Case-insensitive glob match: <c>*</c> any run, <c>?</c> one character.</summary>
    internal static bool Glob(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
