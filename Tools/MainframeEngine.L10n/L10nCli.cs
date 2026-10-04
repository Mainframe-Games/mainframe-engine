using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using MainframeEngine.L10n.Extraction;
using MainframeEngine.L10n.Gettext;
using MainframeEngine.Localization;

namespace MainframeEngine.L10n;

/// <summary>The <c>mf-l10n</c> commands. <see cref="Run"/> is the whole program (tests call it in-process).</summary>
internal static class L10nCli
{
    public const string Usage = """
        mf-l10n — Mainframe Engine localization tool (docs/design/localization.md)

        Locale folders: <dir>/<locale>/LC_MESSAGES/<domain>.po|.mo (domain "messages" unless --domain).

          extract  -o <out.pot> [--root <dir>] [--rml <file|dir>]... [--scenes <file|dir>]... [--include <code.pot>]...
                   [--assembly <game.dll>]... [--translatable Type.Property]... [--project <name>]
                   Extract RML text and [Export(Translatable)] scene strings, merge the C# extractor's templates
                   (--include; their references are rebased onto --root) and write one template.
          merge    -o <out.pot> <a.pot> <b.pot>...            Merge templates (msgcat).
          update   --pot <pot> --dir <dir> [--create <locale,...>] [--domain <name>]
                   Merge the template into every <locale>.po (msgmerge, exact matches; removed messages become
                   obsolete #~ entries). --create adds catalogs for new locales. The qps folder is skipped.
          pseudo   --pot <pot> (--dir <dir> | -o <qps.po>) [--charset full|latin1] [--expansion 0.3] [--compile]
                   Generate the qps pseudo-locale: accented, ~30% longer, [bracketed].
          compile  (<file.po> [-o <file.mo>] | --dir <dir> [--out-dir <dir>]) [--use-fuzzy] [--strict] [--domain <name>]
                   Compile .po to GNU .mo (identical to msgfmt) after checking placeholders and plural counts.
          check    --dir <dir> [--msgfmt] [--domain <name>]
                   Fail if a committed .mo is missing or stale, or a translation breaks its placeholders.
                   --msgfmt also runs GNU msgfmt -c (when on PATH) and requires byte-identical output.
          stats    --dir <dir> [--pot <pot>] [--domain <name>]       Translation coverage per locale.
        """;

    private static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--compile", "--use-fuzzy", "--strict", "--msgfmt", "--help", "-h",
    };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            var command = new CommandLine(args[1..], Flags);
            var result = args[0] switch
            {
                "extract" => Extract(command, output, error),
                "merge" => Merge(command, output),
                "update" => Update(command, output),
                "pseudo" => Pseudo(command, output, error),
                "compile" => Compile(command, output, error),
                "check" => Check(command, output, error),
                "stats" => Stats(command, output),
                _ => throw new UsageException($"unknown command '{args[0]}'"),
            };
            return result;
        }
        catch (UsageException e)
        {
            error.WriteLine($"mf-l10n: {e.Message}");
            error.WriteLine("Run 'mf-l10n help' for usage.");
            return 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidDataException
                                      or ArgumentException or System.Text.Json.JsonException or BadImageFormatException)
        {
            error.WriteLine($"mf-l10n: {e.Message}");
            return 1;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // extract / merge
    // ------------------------------------------------------------------------------------------------

    private static int Extract(CommandLine command, TextWriter output, TextWriter error)
    {
        var outputPath = command.Required("--output");
        var root = Path.GetFullPath(command.Option("--root") ?? Directory.GetCurrentDirectory());
        var rml = command.Many("--rml");
        var scenes = command.Many("--scenes");
        var includes = command.Many("--include");
        var assemblies = command.Many("--assembly");
        var translatable = command.Many("--translatable");
        var project = command.Option("--project") ?? new DirectoryInfo(root).Name;
        command.EnsureAllUsed();
        if (rml.Count + scenes.Count + includes.Count == 0)
            throw new UsageException("nothing to extract: give --rml, --scenes and/or --include");

        var builder = new TemplateBuilder();
        foreach (var include in includes)
        {
            var template = PoParser.ParseFile(include);
            foreach (var entry in template.Messages)
            {
                var rebased = entry.CloneTemplate();
                rebased.References.Clear();
                foreach (var reference in entry.References)
                    rebased.References.Add(Rebase(reference, include, root));
                builder.Add(rebased);
            }

            output.WriteLine($"  {Relative(include, root)}: {template.Messages.Count()} messages (C#)");
        }

        var rmlCount = 0;
        foreach (var file in EnumerateFiles(rml, RmlExtractor.Extensions))
            rmlCount += RmlExtractor.Extract(file, Relative(file, root), builder);

        var sceneCount = 0;
        if (scenes.Count > 0)
        {
            var index = TranslatablePropertyIndex.FromRegistry(assemblies, translatable);
            var extractor = new SceneExtractor(index, root);
            foreach (var file in EnumerateFiles(scenes, SceneExtractor.Extensions))
                sceneCount += extractor.Extract(file, Relative(file, root), builder);
            if (extractor.UnknownTypes.Count > 0)
            {
                error.WriteLine($"mf-l10n: warning: {extractor.UnknownTypes.Count} scene type(s) are not registered, so their " +
                                $"translatable properties are unknown (pass --assembly <game.dll>): {string.Join(", ", extractor.UnknownTypes)}");
            }
        }

        foreach (var warning in builder.Warnings)
            error.WriteLine($"mf-l10n: warning: {warning}");

        var catalog = builder.Build(project);
        PoWriter.WriteFile(catalog, outputPath);
        output.WriteLine($"Wrote {outputPath}: {builder.Count} messages ({rmlCount} RML runs, {sceneCount} scene strings)");
        return 0;
    }

    private static int Merge(CommandLine command, TextWriter output)
    {
        var outputPath = command.Required("--output");
        var project = command.Option("--project");
        command.EnsureAllUsed();
        if (command.Positionals.Count == 0)
            throw new UsageException("merge needs input templates");
        var builder = new TemplateBuilder();
        string? firstProject = null;
        foreach (var input in command.Positionals)
        {
            var template = PoParser.ParseFile(input);
            firstProject ??= template.GetHeader("Project-Id-Version");
            foreach (var entry in template.Messages)
                builder.Add(entry);
        }

        PoWriter.WriteFile(builder.Build(project ?? firstProject ?? "PACKAGE"), outputPath);
        output.WriteLine($"Wrote {outputPath}: {builder.Count} messages");
        return 0;
    }

    // ------------------------------------------------------------------------------------------------
    // update
    // ------------------------------------------------------------------------------------------------

    private static int Update(CommandLine command, TextWriter output)
    {
        var template = PoParser.ParseFile(command.Required("--pot"));
        var directory = command.Required("--dir");
        var domain = command.Option("--domain") ?? LocalizationOptions.DefaultDomain;
        var create = command.Many("--create");
        command.EnsureAllUsed();

        foreach (var locale in create)
        {
            var normalized = LocaleId.Normalize(locale);
            if (normalized.Length == 0)
                throw new UsageException($"invalid locale '{locale}'");
            var path = PoPath(directory, normalized, domain);
            if (File.Exists(path))
                continue;
            var catalog = NewCatalog(template, normalized);
            PoWriter.WriteFile(catalog, path);
            output.WriteLine($"Created {path}");
        }

        foreach (var (locale, path) in Catalogs(directory, domain, ".po"))
        {
            if (locale == LocaleId.Pseudo)
                continue;
            var catalog = PoParser.ParseFile(path);
            var stats = MergeInto(catalog, template, locale);
            PoWriter.WriteFile(catalog, path);
            output.WriteLine($"{path}: {stats.Kept} kept, {stats.Added} new, {stats.Obsoleted} obsolete");
        }

        return 0;
    }

    /// <summary>A new, empty catalog for <paramref name="locale"/> from a template (msginit).</summary>
    internal static PoCatalog NewCatalog(PoCatalog template, string locale)
    {
        var catalog = new PoCatalog();
        catalog.FileComments.Add($"{LocaleId.GetCulture(locale).EnglishName} translation of {template.GetHeader("Project-Id-Version") ?? "the project"}.");
        catalog.SetHeader("Project-Id-Version", template.GetHeader("Project-Id-Version") ?? "PACKAGE VERSION");
        catalog.SetHeader("Language", locale);
        catalog.SetHeader("MIME-Version", "1.0");
        catalog.SetHeader("Content-Type", "text/plain; charset=UTF-8");
        catalog.SetHeader("Content-Transfer-Encoding", "8bit");
        catalog.SetHeader("Plural-Forms", PluralForms.For(locale));
        MergeInto(catalog, template, locale);
        return catalog;
    }

    /// <summary>
    /// msgmerge: messages follow the template's order and comments; translations, translator comments and fuzziness are
    /// kept for messages still in the template; messages no longer in it become obsolete.
    /// </summary>
    internal static (int Kept, int Added, int Obsoleted) MergeInto(PoCatalog catalog, PoCatalog template, string locale)
    {
        var plurals = catalog.PluralCount ?? PluralForms.Count(PluralForms.For(locale));
        var existing = catalog.ByKey();
        var header = catalog.Header;
        var oldObsolete = catalog.Entries.Where(static e => e.Obsolete).ToList();
        // A message that comes back gets its obsolete translation back (as msgmerge does).
        foreach (var obsolete in oldObsolete)
        {
            if (!existing.ContainsKey(obsolete.Key) && obsolete.HasTranslation)
            {
                existing[obsolete.Key] = obsolete;
            }
        }

        var merged = new List<PoEntry>();
        if (header is not null)
            merged.Add(header);

        int kept = 0, added = 0;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in template.Messages)
        {
            if (source.Obsolete)
                continue;
            var entry = source.CloneTemplate();
            used.Add(source.Key);
            if (existing.TryGetValue(source.Key, out var old))
            {
                entry.TranslatorComments.AddRange(old.TranslatorComments);
                if (old.IsFuzzy)
                    entry.SetFlag("fuzzy", true);
                if ((old.IdPlural is null) == (entry.IdPlural is null))
                {
                    entry.Translations.AddRange(old.Translations);
                }
                else
                {
                    // The message gained or lost a plural: keep the old text for the translator, marked fuzzy.
                    entry.Translations.Add(old.Translations.Count > 0 ? old.Translations[0] : string.Empty);
                    entry.SetFlag("fuzzy", entry.Translations[0].Length > 0);
                }

                kept++;
            }
            else
            {
                added++;
            }

            var forms = entry.IdPlural is null ? 1 : plurals;
            while (entry.Translations.Count < forms)
                entry.Translations.Add(string.Empty);
            if (entry.Translations.Count > forms)
                entry.Translations.RemoveRange(forms, entry.Translations.Count - forms);
            merged.Add(entry);
        }

        var obsoleted = 0;
        foreach (var (key, old) in existing)
        {
            if (used.Contains(key) || !old.HasTranslation || old.Obsolete)
                continue;
            old.Obsolete = true;
            old.References.Clear();
            old.PreviousLines.Clear();
            merged.Add(old);
            obsoleted++;
        }

        foreach (var old in oldObsolete)
        {
            if (!used.Contains(old.Key) && !merged.Exists(e => e.Obsolete && e.Key == old.Key))
                merged.Add(old);
        }

        catalog.Entries.Clear();
        catalog.Entries.AddRange(merged);
        return (kept, added, obsoleted);
    }

    // ------------------------------------------------------------------------------------------------
    // pseudo
    // ------------------------------------------------------------------------------------------------

    private static int Pseudo(CommandLine command, TextWriter output, TextWriter error)
    {
        var template = PoParser.ParseFile(command.Required("--pot"));
        var directory = command.Option("--dir");
        var outputPath = command.Option("--output");
        var domain = command.Option("--domain") ?? LocalizationOptions.DefaultDomain;
        var charset = PseudoLocalizer.ParseCharset(command.Option("--charset") ?? "full");
        var expansion = PseudoLocalizer.ParseExpansion(command.Option("--expansion") ?? "0.3");
        var compile = command.Flag("--compile");
        command.EnsureAllUsed();
        if ((directory is null) == (outputPath is null))
            throw new UsageException("pseudo needs exactly one of --dir and -o");

        var path = outputPath ?? PoPath(directory!, LocaleId.Pseudo, domain);
        var catalog = PseudoLocalizer.CreateCatalog(template, charset, expansion);
        PoWriter.WriteFile(catalog, path);
        output.WriteLine($"Wrote {path}: {catalog.Messages.Count()} messages");
        return compile ? CompileOne(path, Path.ChangeExtension(path, ".mo"), false, false, output, error) : 0;
    }

    // ------------------------------------------------------------------------------------------------
    // compile / check
    // ------------------------------------------------------------------------------------------------

    private static int Compile(CommandLine command, TextWriter output, TextWriter error)
    {
        var directory = command.Option("--dir");
        var outDirectory = command.Option("--out-dir");
        var outputPath = command.Option("--output");
        var domain = command.Option("--domain") ?? LocalizationOptions.DefaultDomain;
        var useFuzzy = command.Flag("--use-fuzzy");
        var strict = command.Flag("--strict");
        command.EnsureAllUsed();

        if (directory is not null)
        {
            if (command.Positionals.Count > 0 || outputPath is not null)
                throw new UsageException("compile takes either --dir or a .po file");
            var result = 0;
            foreach (var (locale, path) in Catalogs(directory, domain, ".po"))
            {
                var target = Path.Combine(outDirectory ?? directory, locale, "LC_MESSAGES", domain + ".mo");
                result = Math.Max(result, CompileOne(path, target, useFuzzy, strict, output, error));
            }

            return result;
        }

        if (command.Positionals.Count != 1)
            throw new UsageException("compile needs one .po file (or --dir)");
        var po = command.Positionals[0];
        return CompileOne(po, outputPath ?? Path.ChangeExtension(po, ".mo"), useFuzzy, strict, output, error);
    }

    /// <summary>Validates and compiles one catalog; writes the .mo only when its bytes change.</summary>
    internal static int CompileOne(string poPath, string moPath, bool useFuzzy, bool strict, TextWriter output, TextWriter error)
    {
        var catalog = PoParser.ParseFile(poPath);
        var (errors, warnings) = Validate(catalog, poPath, error, useFuzzy);
        if (errors > 0 || (strict && warnings > 0))
        {
            error.WriteLine($"mf-l10n: {poPath}: {errors} error(s), {warnings} warning(s); not compiled");
            return 1;
        }

        var bytes = MoFormat.Write(catalog, useFuzzy);
        var directory = Path.GetDirectoryName(Path.GetFullPath(moPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        if (!File.Exists(moPath) || !File.ReadAllBytes(moPath).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(moPath, bytes);
        else
            File.SetLastWriteTimeUtc(moPath, DateTime.UtcNow); // same bytes: still newer than the .po for incremental builds
        var messages = MoFormat.SelectMessages(catalog, useFuzzy).Count(static m => m.Key.Length > 0);
        output.WriteLine($"{poPath} -> {moPath}: {messages} messages");
        return 0;
    }

    private static (int Errors, int Warnings) Validate(PoCatalog catalog, string poPath, TextWriter error, bool useFuzzy = false)
    {
        int errors = 0, warnings = 0;
        var plurals = catalog.PluralCount;
        if (plurals is null && catalog.Messages.Any(static e => e.IsPlural && !e.Obsolete))
        {
            error.WriteLine($"{poPath}: error: plural messages but no valid Plural-Forms header");
            errors++;
        }

        foreach (var entry in catalog.Messages)
        {
            if (entry.IsFuzzy && !useFuzzy)
                continue; // not compiled, so a work-in-progress translation cannot break the build
            foreach (var issue in Placeholders.Check(entry, plurals))
            {
                error.WriteLine($"{poPath}: {(issue.IsError ? "error" : "warning")}: {issue.Message}");
                if (issue.IsError)
                    errors++;
                else
                    warnings++;
            }
        }

        return (errors, warnings);
    }

    private static int Check(CommandLine command, TextWriter output, TextWriter error)
    {
        var directory = command.Required("--dir");
        var domain = command.Option("--domain") ?? LocalizationOptions.DefaultDomain;
        var msgfmt = command.Flag("--msgfmt");
        command.EnsureAllUsed();

        var failures = 0;
        var checkedCount = 0;
        var msgfmtAvailable = msgfmt && MsgfmtAvailable();
        if (msgfmt && !msgfmtAvailable)
            output.WriteLine("msgfmt not found on PATH; skipping the GNU comparison");

        foreach (var (locale, poPath) in Catalogs(directory, domain, ".po"))
        {
            checkedCount++;
            var catalog = PoParser.ParseFile(poPath);
            var (errors, _) = Validate(catalog, poPath, error);
            failures += errors;
            var expected = MoFormat.Write(catalog);
            var moPath = Path.ChangeExtension(poPath, ".mo");
            if (!File.Exists(moPath))
            {
                error.WriteLine($"{moPath}: error: missing (run just l10n-compile)");
                failures++;
            }
            else if (!File.ReadAllBytes(moPath).AsSpan().SequenceEqual(expected))
            {
                error.WriteLine($"{moPath}: error: stale, does not match {Path.GetFileName(poPath)} (run just l10n-compile)");
                failures++;
            }

            if (msgfmtAvailable)
                failures += CompareWithMsgfmt(poPath, expected, error);
            output.WriteLine($"{locale}: checked");
        }

        output.WriteLine(failures == 0 ? $"{checkedCount} catalog(s) OK" : $"{failures} problem(s)");
        return failures == 0 ? 0 : 1;
    }

    internal static bool MsgfmtAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("msgfmt", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Runs <c>msgfmt -c</c> on the catalog and compares its output with ours; returns the number of failures.</summary>
    internal static int CompareWithMsgfmt(string poPath, byte[] expected, TextWriter error)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"mf-l10n-{Guid.NewGuid():N}.mo");
        try
        {
            var start = new ProcessStartInfo("msgfmt")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add(temp);
            start.ArgumentList.Add(poPath);
            using var process = Process.Start(start) ?? throw new IOException("could not start msgfmt");
            var stderr = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                error.WriteLine($"{poPath}: error: msgfmt -c failed:\n{stderr.TrimEnd()}");
                return 1;
            }

            var actual = File.ReadAllBytes(temp);
            if (actual.AsSpan().SequenceEqual(expected) || MatchesLegacyMsgfmt(poPath, actual))
                return 0;

            error.WriteLine($"{poPath}: error: msgfmt output differs from mf-l10n's");
            return 1;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    // gettext 0.21 and older (e.g. Ubuntu 24.04) size a two-message hash table 5 instead of 3; everything else is the
    // same, so their output must equal ours written with that size.
    private static bool MatchesLegacyMsgfmt(string poPath, byte[] msgfmtOutput)
    {
        var messages = MoFormat.SelectMessages(PoParser.ParseFile(poPath));
        var legacySize = MoFormat.LegacyHashTableSize(messages.Count);
        return legacySize != MoFormat.HashTableSize(messages.Count) &&
               msgfmtOutput.AsSpan().SequenceEqual(MoFormat.Write(messages, legacySize));
    }

    // ------------------------------------------------------------------------------------------------
    // stats
    // ------------------------------------------------------------------------------------------------

    private static int Stats(CommandLine command, TextWriter output)
    {
        var directory = command.Required("--dir");
        var domain = command.Option("--domain") ?? LocalizationOptions.DefaultDomain;
        var potPath = command.Option("--pot");
        command.EnsureAllUsed();
        var template = potPath is null ? null : PoParser.ParseFile(potPath).ByKey();

        output.WriteLine("locale      translated   fuzzy  untranslated   coverage");
        foreach (var (locale, path) in Catalogs(directory, domain, ".po"))
        {
            var messages = PoParser.ParseFile(path).ByKey();
            var keys = template?.Keys ?? (IEnumerable<string>)messages.Keys;
            int total = 0, translated = 0, fuzzy = 0;
            foreach (var key in keys)
            {
                total++;
                if (!messages.TryGetValue(key, out var entry) || !entry.HasTranslation)
                    continue;
                if (entry.IsFuzzy)
                    fuzzy++;
                else if (entry.IsFullyTranslated)
                    translated++;
            }

            var coverage = total == 0 ? 100.0 : 100.0 * translated / total;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{locale,-10} {translated,11} {fuzzy,7} {total - translated - fuzzy,13} {coverage,9:0.0}%"));
        }

        return 0;
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    internal static string PoPath(string directory, string locale, string domain) =>
        Path.Combine(directory, locale, "LC_MESSAGES", domain + ".po");

    /// <summary>The <c>&lt;dir&gt;/&lt;locale&gt;/LC_MESSAGES/&lt;domain&gt;&lt;extension&gt;</c> files, by locale.</summary>
    internal static List<(string Locale, string Path)> Catalogs(string directory, string domain, string extension)
    {
        var result = new List<(string, string)>();
        if (!Directory.Exists(directory))
            return result;
        foreach (var folder in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(folder, "LC_MESSAGES", domain + extension);
            if (!File.Exists(path))
                continue;
            var name = Path.GetFileName(folder);
            var locale = LocaleId.Normalize(name);
            // The runtime only looks in the normalised folder (case-sensitive on Linux): reject anything else.
            if (locale != name)
            {
                throw new InvalidDataException(locale.Length == 0
                    ? $"{folder}: '{name}' is not a locale name"
                    : $"{folder}: rename the folder to '{locale}' (the runtime looks for gettext-style names)");
            }

            result.Add((locale, path));
        }

        return result;
    }

    private static SortedSet<string> EnumerateFiles(IEnumerable<string> inputs, string[] extensions)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (File.Exists(input))
            {
                files.Add(Path.GetFullPath(input));
                continue;
            }

            if (!Directory.Exists(input))
                throw new FileNotFoundException($"not found: {input}");
            foreach (var file in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(input, file).Replace('\\', '/');
                if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal)
                    || relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    files.Add(Path.GetFullPath(file));
            }
        }

        return files;
    }

    /// <summary>A project-relative reference path with forward slashes.</summary>
    internal static string Relative(string path, string root) =>
        Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');

    /// <summary>
    /// Rebases a <c>path:line</c> reference from an included template onto <paramref name="root"/>. The GetText.NET
    /// extractor writes paths relative to the template's own path treated as a folder (<c>out.pot/../src/A.cs</c>);
    /// xgettext writes them relative to where it ran. The first base under which the file exists wins: the template
    /// path, its folder, then the root. References to files that exist nowhere are kept as written.
    /// </summary>
    internal static string Rebase(string reference, string templatePath, string root)
    {
        var colon = reference.LastIndexOf(':');
        var hasLine = colon > 0 && int.TryParse(reference.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _);
        var path = hasLine ? reference[..colon] : reference;
        var template = Path.GetFullPath(templatePath);
        foreach (var baseDirectory in (ReadOnlySpan<string>)[template, Path.GetDirectoryName(template)!, root])
        {
            var candidate = Path.GetFullPath(Path.Combine(baseDirectory, path));
            if (File.Exists(candidate))
            {
                var rebased = Relative(candidate, root);
                return hasLine ? rebased + reference[colon..] : rebased;
            }
        }

        return reference;
    }

    /// <summary>Writes UTF-8 console output (accented pseudo strings print correctly on Windows too).</summary>
    public static int RunConsole(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        return Run(args, Console.Out, Console.Error);
    }
}
