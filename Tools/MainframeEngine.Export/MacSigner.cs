namespace MainframeEngine.Export;

/// <summary>
/// macOS signing and notarization of an exported <c>.app</c> (macOS hosts only: <c>codesign</c>, <c>xcrun</c> and
/// <c>ditto</c> ship with the OS), the same steps as <c>build/macos/sign-app.sh</c> (which the editor's own release still
/// uses): every Mach-O in <c>Contents</c> but the main executable first, deepest first, then the bundle, which signs the main
/// executable and seals the rest; never <c>--deep</c>. Ad hoc (<c>-</c>) by default (Apple Silicon refuses unsigned code);
/// a Developer ID adds the hardened runtime, a secure timestamp and, on the bundle, the .NET entitlements (JIT, unsigned
/// executable memory, library validation off). Then <c>codesign --verify --deep --strict</c>.
/// </summary>
public sealed class MacSigner(ToolRunner run, Action<string>? onLine)
{
    /// <summary>The .NET runtime's entitlements under the hardened runtime.</summary>
    public const string DotnetEntitlements = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
        	<key>com.apple.security.cs.allow-jit</key><true/>
        	<key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
        	<key>com.apple.security.cs.disable-library-validation</key><true/>
        </dict></plist>
        """;

    /// <summary>Whether the file starts with a Mach-O (thin or universal) magic number.</summary>
    public static bool IsMachO(string file)
    {
        Span<byte> magic = stackalloc byte[4];
        try
        {
            using var stream = File.OpenRead(file);
            if (stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false) < 4)
                return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var value = (uint)(magic[0] << 24 | magic[1] << 16 | magic[2] << 8 | magic[3]);
        return value is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA;
    }

    /// <summary>The Mach-O files of the bundle to sign before it, deepest first (symlinks are sealed, never signed).</summary>
    public static IReadOnlyList<string> NestedCode(string app, string mainExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        var main = Path.GetFullPath(Path.Combine(app, "Contents", "MacOS", mainExecutable));
        return
        [
            .. Directory.EnumerateFiles(Path.Combine(app, "Contents"), "*", SearchOption.AllDirectories)
                .Where(f => new FileInfo(f).LinkTarget is null && !string.Equals(Path.GetFullPath(f), main, StringComparison.Ordinal) && IsMachO(f))
                .OrderByDescending(f => f.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(f => f, StringComparer.Ordinal),
        ];
    }

    /// <summary>The <c>codesign</c> arguments for one file (or the bundle, <paramref name="entitlements"/> set).</summary>
    public static IReadOnlyList<string> SignArguments(string identity, string path, string? entitlements)
    {
        var args = new List<string> { "--force", "--sign", identity };
        if (identity != "-")
            args.AddRange(["--timestamp", "--options", "runtime"]);
        if (entitlements is not null)
            args.AddRange(["--entitlements", entitlements]);
        args.Add(path);
        return args;
    }

    /// <summary>Signs and verifies <paramref name="app"/>; returns null, or why it failed.</summary>
    public async Task<string?> SignAsync(string app, string mainExecutable, string identity, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        string? entitlements = null;
        try
        {
            if (identity != "-")
            {
                entitlements = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "dotnet.entitlements");
                await File.WriteAllTextAsync(entitlements, DotnetEntitlements, cancellationToken).ConfigureAwait(false);
            }

            foreach (var file in NestedCode(app, mainExecutable))
            {
                var nested = await run("codesign", SignArguments(identity, file, null), null, null, null, cancellationToken).ConfigureAwait(false);
                if (!nested.Succeeded)
                    return Failure("codesign", nested, file);
            }

            var bundle = await run("codesign", SignArguments(identity, app, entitlements), null, null, null, cancellationToken).ConfigureAwait(false);
            if (!bundle.Succeeded)
                return Failure("codesign", bundle, app);
            var verify = await run("codesign", ["--verify", "--deep", "--strict", "--verbose=2", app], null, onLine, null, cancellationToken)
                .ConfigureAwait(false);
            return verify.Succeeded ? null : Failure("codesign --verify", verify, app);
        }
        finally
        {
            if (entitlements is not null)
                Archiver.TryDelete(entitlements);
        }
    }

    /// <summary>Zips the bundle with <c>ditto</c> (no resource forks or extended attributes; the zip holds <c>&lt;Name&gt;.app</c>).</summary>
    public async Task<string?> ZipAsync(string app, string archive, CancellationToken cancellationToken)
    {
        var partial = archive + Archiver.PartialSuffix;
        Archiver.TryDelete(partial);
        var result = await run("ditto", ["-c", "-k", "--norsrc", "--noextattr", "--keepParent", app, partial],
            Path.GetDirectoryName(Path.GetFullPath(app)), onLine, null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            Archiver.TryDelete(partial);
            return Failure("ditto", result, app);
        }

        File.Move(partial, archive, overwrite: true);
        return null;
    }

    /// <summary>Submits <paramref name="archive"/> to Apple's notary service and staples the ticket to <paramref name="app"/>.</summary>
    public async Task<string?> NotarizeAsync(string app, string archive, string profile, CancellationToken cancellationToken)
    {
        var submit = await run("xcrun", ["notarytool", "submit", archive, "--keychain-profile", profile, "--wait"], null, onLine, null, cancellationToken)
            .ConfigureAwait(false);
        if (!submit.Succeeded)
            return Failure("xcrun notarytool", submit, archive);
        var staple = await run("xcrun", ["stapler", "staple", app], null, onLine, null, cancellationToken).ConfigureAwait(false);
        return staple.Succeeded ? null : Failure("xcrun stapler", staple, app);
    }

    private string Failure(string tool, ProcessResult result, string what)
    {
        foreach (var line in result.Output)
            onLine?.Invoke(line);
        var reason = result.StartError ?? (result.Cancelled ? "cancelled" : $"exit code {result.ExitCode}");
        return $"{tool} failed for {Path.GetFileName(what)} ({reason}).";
    }
}
