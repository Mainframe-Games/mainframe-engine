using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>One upgrade step of <c>project.mfproj</c>: rewrites a format-<see cref="From"/> document into format From+1.</summary>
public readonly record struct ProjectMigration(int From, Action<JsonObject> Upgrade);

/// <summary>
/// Reads and writes <c>project.mfproj</c>. The file carries a <c>format</c> number; on load, documents older than
/// <see cref="Current"/> run through <see cref="Migrations"/> one step at a time (each step edits the JSON tree), and
/// newer ones are rejected. Saving always writes the current format.
/// </summary>
public static class ProjectSettingsFormat
{
    /// <summary>The format this engine writes. Bump it and add a <see cref="ProjectMigration"/> when the layout changes.</summary>
    public const int Current = 3;

    /// <summary>The engine's upgrade steps, ordered by <see cref="ProjectMigration.From"/>.</summary>
    public static IReadOnlyList<ProjectMigration> Migrations { get; } =
    [
        // 1 → 2: the top-level "steamAppId" moved into the "steam" section (with the demo app id and start-up options).
        new(1, static root =>
        {
            if (root["steamAppId"] is not { } appId)
                return;
            root.Remove("steamAppId");
            if (root["steam"] is not JsonObject steam)
                root["steam"] = steam = new JsonObject();
            steam["appId"] ??= appId;
        }),

        // 2 → 3: the "ui" section (ADR 0181). New projects scale the UI with the screen from a 1080p reference; older
        // projects keep the size they were made with: dp = the content scale.
        new(2, static root =>
        {
            if (root["ui"] is not JsonObject ui)
                root["ui"] = ui = new JsonObject();
            ui["scaleMode"] ??= nameof(UiScalingMode.ConstantPixelSize);
        }),
    ];

    /// <summary>Parses <paramref name="json"/> with the engine's migrations.</summary>
    public static ProjectSettings Parse(ReadOnlySpan<byte> json, string source) => Parse(json, source, Current, Migrations);

    /// <summary>Parses with an explicit target format and migration chain (tests and tools).</summary>
    public static ProjectSettings Parse(ReadOnlySpan<byte> json, string source, int currentFormat, IReadOnlyList<ProjectMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        try
        {
            return ParseCore(json, source, currentFormat, migrations);
        }
        catch (ArgumentException e)
        {
            // JsonObject materializes properties lazily and throws ArgumentException for a duplicate key; a migration
            // may throw one too. Every file problem surfaces as InvalidDataException.
            throw new InvalidDataException($"'{source}' is invalid: {e.Message}", e);
        }
    }

    private static ProjectSettings ParseCore(ReadOnlySpan<byte> json, string source, int currentFormat, IReadOnlyList<ProjectMigration> migrations)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: SceneFormat.ReadOptions) as JsonObject
                   ?? throw new InvalidDataException($"'{source}' is not a JSON object.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"'{source}' is not valid JSON: {e.Message}", e);
        }

        var reader = new Reader(source);
        var format = reader.Int(root, "format", "format", 1);
        if (format > currentFormat)
            throw new InvalidDataException($"'{source}' has project format {format}; this engine reads up to {currentFormat}. Update the engine.");
        if (format < 1)
            throw new InvalidDataException($"'{source}' has an invalid project format {format}.");
        Upgrade(root, format, currentFormat, migrations, source);
        return reader.Read(root);
    }

    /// <summary>Runs the steps from <paramref name="format"/> to <paramref name="currentFormat"/> on <paramref name="root"/>.</summary>
    public static void Upgrade(JsonObject root, int format, int currentFormat, IReadOnlyList<ProjectMigration> migrations, string source)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(migrations);
        while (format < currentFormat)
        {
            var step = -1;
            for (var i = 0; i < migrations.Count; i++)
            {
                if (migrations[i].From == format)
                {
                    step = i;
                    break;
                }
            }

            if (step < 0)
                throw new InvalidDataException($"'{source}': no migration from project format {format} to {format + 1}.");
            migrations[step].Upgrade(root);
            format++;
        }

        root["format"] = currentFormat;
    }

    /// <summary>Serializes <paramref name="settings"/> (current format, only non-default values below the top level).</summary>
    public static byte[] Write(ProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, SceneFormat.WriteOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("format", Current);
            w.WriteString("name", settings.Name);
            w.WriteString("engineVersion", settings.EngineVersion);
            if (settings.Version.Length > 0)
                w.WriteString("version", settings.Version);
            if (settings.MainScene is { Length: > 0 } main)
                w.WriteString("mainScene", main);
            if (settings.Assemblies.Count > 0)
                WriteStrings(w, "assemblies", settings.Assemblies);
            if (settings.IsDemo)
                w.WriteBoolean("isDemo", true);
            WriteSteam(w, settings.Steam);
            WriteWindow(w, settings.Window);
            WritePhysics(w, settings.Physics);
            WriteInput(w, settings.Input);
            WriteAudio(w, settings.Audio);
            WriteLocalization(w, settings.Localization);
            WriteRendering(w, settings.Rendering);
            WriteUi(w, settings.Ui);
            WriteAutoloads(w, settings.Autoloads);
            WritePlayInstances(w, settings.PlayInstances);
            w.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    // ----------------------------------------------------------------------------------------------------
    // Writing (sections are omitted when every value is the default)
    // ----------------------------------------------------------------------------------------------------

    private static void WriteSteam(Utf8JsonWriter w, SteamProjectSettings s)
    {
        var d = new SteamProjectSettings();
        if (s.AppId == d.AppId && s.DemoAppId == d.DemoAppId && s.DevAppIdFile == d.DevAppIdFile && s.RestartThroughSteam == d.RestartThroughSteam)
            return;
        w.WriteStartObject("steam");
        if (s.AppId != d.AppId)
            w.WriteNumber("appId", s.AppId);
        if (s.DemoAppId != d.DemoAppId)
            w.WriteNumber("demoAppId", s.DemoAppId);
        if (s.DevAppIdFile != d.DevAppIdFile)
            w.WriteBoolean("devAppIdFile", s.DevAppIdFile);
        if (s.RestartThroughSteam != d.RestartThroughSteam)
            w.WriteBoolean("restartThroughSteam", s.RestartThroughSteam);
        w.WriteEndObject();
    }

    private static void WriteWindow(Utf8JsonWriter w, WindowSettings s)
    {
        var d = new WindowSettings();
        if (s.Title == d.Title && s.Width == d.Width && s.Height == d.Height && s.VSync == d.VSync && s.MaxFps == d.MaxFps && s.Icon == d.Icon
            && s.StretchMode == d.StretchMode && s.StretchAspect == d.StretchAspect && s.StretchScale == d.StretchScale
            && s.StretchScaleMode == d.StretchScaleMode && s.ContentScale == d.ContentScale)
            return;
        w.WriteStartObject("window");
        if (s.Title is not null)
            w.WriteString("title", s.Title);
        if (s.Width != d.Width)
            w.WriteNumber("width", s.Width);
        if (s.Height != d.Height)
            w.WriteNumber("height", s.Height);
        if (s.VSync != d.VSync)
            w.WriteBoolean("vsync", s.VSync);
        if (s.MaxFps != d.MaxFps)
            w.WriteNumber("maxFps", s.MaxFps);
        if (s.Icon is not null)
            w.WriteString("icon", s.Icon);
        if (s.StretchMode != d.StretchMode)
            w.WriteString("stretchMode", s.StretchMode.ToString());
        if (s.StretchAspect != d.StretchAspect)
            w.WriteString("stretchAspect", s.StretchAspect.ToString());
        if (s.StretchScale != d.StretchScale)
            w.WriteNumber("stretchScale", s.StretchScale);
        if (s.StretchScaleMode != d.StretchScaleMode)
            w.WriteString("stretchScaleMode", s.StretchScaleMode.ToString());
        if (s.ContentScale != d.ContentScale)
            w.WriteNumber("contentScale", s.ContentScale);
        w.WriteEndObject();
    }

    private static void WritePhysics(Utf8JsonWriter w, PhysicsProjectSettings s)
    {
        var d = new PhysicsProjectSettings();
        var p3 = s.Physics3D;
        var d3 = d.Physics3D;
        var p2 = s.Physics2D;
        var d2 = d.Physics2D;
        var changed3 = p3.Gravity != d3.Gravity || p3.SubstepCount != d3.SubstepCount || p3.SolverIterations != d3.SolverIterations
                       || p3.RelaxationIterations != d3.RelaxationIterations || p3.AllowDeactivation != d3.AllowDeactivation
                       || p3.MultiThreaded != d3.MultiThreaded || p3.Deterministic != d3.Deterministic;
        var changed2 = p2.Gravity != d2.Gravity || p2.PixelsPerMeter != d2.PixelsPerMeter || p2.SubstepCount != d2.SubstepCount
                       || p2.AllowSleep != d2.AllowSleep || p2.EnableContinuous != d2.EnableContinuous;
        if (s.TicksPerSecond == d.TicksPerSecond && s.MaxStepsPerFrame == d.MaxStepsPerFrame && !changed3 && !changed2)
            return;

        w.WriteStartObject("physics");
        if (s.TicksPerSecond != d.TicksPerSecond)
            w.WriteNumber("ticksPerSecond", s.TicksPerSecond);
        if (s.MaxStepsPerFrame != d.MaxStepsPerFrame)
            w.WriteNumber("maxStepsPerFrame", s.MaxStepsPerFrame);
        if (changed3)
        {
            w.WriteStartObject("3d");
            if (p3.Gravity != d3.Gravity)
                WriteFloats(w, "gravity", [p3.Gravity.X, p3.Gravity.Y, p3.Gravity.Z]);
            if (p3.SubstepCount != d3.SubstepCount)
                w.WriteNumber("substeps", p3.SubstepCount);
            if (p3.SolverIterations != d3.SolverIterations)
                w.WriteNumber("solverIterations", p3.SolverIterations);
            if (p3.RelaxationIterations != d3.RelaxationIterations)
                w.WriteNumber("relaxationIterations", p3.RelaxationIterations);
            if (p3.AllowDeactivation != d3.AllowDeactivation)
                w.WriteBoolean("allowDeactivation", p3.AllowDeactivation);
            if (p3.MultiThreaded != d3.MultiThreaded)
                w.WriteBoolean("multiThreaded", p3.MultiThreaded);
            if (p3.Deterministic != d3.Deterministic)
                w.WriteBoolean("deterministic", p3.Deterministic);
            w.WriteEndObject();
        }

        if (changed2)
        {
            w.WriteStartObject("2d");
            if (p2.Gravity != d2.Gravity)
                WriteFloats(w, "gravity", [p2.Gravity.X, p2.Gravity.Y]);
            if (p2.PixelsPerMeter != d2.PixelsPerMeter)
                w.WriteNumber("pixelsPerMeter", p2.PixelsPerMeter);
            if (p2.SubstepCount != d2.SubstepCount)
                w.WriteNumber("substeps", p2.SubstepCount);
            if (p2.AllowSleep != d2.AllowSleep)
                w.WriteBoolean("allowSleep", p2.AllowSleep);
            if (p2.EnableContinuous != d2.EnableContinuous)
                w.WriteBoolean("continuous", p2.EnableContinuous);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static void WriteInput(Utf8JsonWriter w, InputMap map)
    {
        if (map.Actions.Count == 0)
            return;
        w.WriteStartObject("input");
        foreach (var action in map.Actions)
        {
            w.WriteStartObject(action.Name);
            if (action.Deadzone != InputAction.DefaultDeadzone)
                w.WriteNumber("deadzone", action.Deadzone);
            w.WriteStartArray("bindings");
            foreach (var binding in action.Bindings)
                w.WriteStringValue(binding.ToString());
            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static void WriteAudio(Utf8JsonWriter w, AudioProjectSettings s)
    {
        var d = new AudioProjectSettings();
        if (s.Enabled == d.Enabled && s.BusLayout == d.BusLayout && s.SampleRate == d.SampleRate && s.BufferMilliseconds == d.BufferMilliseconds)
            return;
        w.WriteStartObject("audio");
        if (s.Enabled != d.Enabled)
            w.WriteBoolean("enabled", s.Enabled);
        if (s.BusLayout != d.BusLayout)
        {
            if (s.BusLayout is null)
                w.WriteNull("busLayout");
            else
                w.WriteString("busLayout", s.BusLayout);
        }

        if (s.SampleRate != d.SampleRate)
            w.WriteNumber("sampleRate", s.SampleRate);
        if (s.BufferMilliseconds != d.BufferMilliseconds)
            w.WriteNumber("bufferMs", s.BufferMilliseconds);
        w.WriteEndObject();
    }

    private static void WriteLocalization(Utf8JsonWriter w, LocalizationProjectSettings s)
    {
        var d = new LocalizationProjectSettings();
        if (s.DefaultLocale == d.DefaultLocale && s.SourceLocale == d.SourceLocale && s.Fallbacks.Count == 0
            && s.LocaleDirectory == d.LocaleDirectory && s.Domain == d.Domain)
            return;
        w.WriteStartObject("localization");
        if (s.DefaultLocale is not null)
            w.WriteString("defaultLocale", s.DefaultLocale);
        if (s.SourceLocale != d.SourceLocale)
            w.WriteString("sourceLocale", s.SourceLocale);
        if (s.Fallbacks.Count > 0)
            WriteStrings(w, "fallbacks", s.Fallbacks);
        if (s.LocaleDirectory != d.LocaleDirectory)
            w.WriteString("directory", s.LocaleDirectory);
        if (s.Domain != d.Domain)
            w.WriteString("domain", s.Domain);
        w.WriteEndObject();
    }

    private static void WriteRendering(Utf8JsonWriter w, RenderingProjectSettings s)
    {
        var d = new RenderingProjectSettings();
        if (s.Exposure == d.Exposure && s.Shadows == d.Shadows && s.CanvasClearColor is null && s.AntiAliasing == d.AntiAliasing
            && s.TaaSharpness == d.TaaSharpness && s.WaterSsr == d.WaterSsr && s.Scaling3DMode == d.Scaling3DMode
            && s.Scaling3DScale == d.Scaling3DScale && s.FsrSharpness == d.FsrSharpness)
            return;
        w.WriteStartObject("rendering");
        if (s.Exposure != d.Exposure)
            w.WriteNumber("exposure", s.Exposure);
        if (s.Shadows != d.Shadows)
            w.WriteString("shadows", s.Shadows.ToString());
        if (s.AntiAliasing != d.AntiAliasing)
            w.WriteString("antiAliasing", s.AntiAliasing.ToString());
        if (s.TaaSharpness != d.TaaSharpness)
            w.WriteNumber("taaSharpness", s.TaaSharpness);
        if (s.WaterSsr != d.WaterSsr)
            w.WriteString("waterSsr", s.WaterSsr.ToString());
        if (s.Scaling3DMode != d.Scaling3DMode)
            w.WriteString("scaling3DMode", s.Scaling3DMode.ToString());
        if (s.Scaling3DScale != d.Scaling3DScale)
            w.WriteNumber("scaling3DScale", s.Scaling3DScale);
        if (s.FsrSharpness != d.FsrSharpness)
            w.WriteNumber("fsrSharpness", s.FsrSharpness);
        if (s.CanvasClearColor is { } c)
            WriteFloats(w, "canvasClearColor", [c.X, c.Y, c.Z, c.W]);
        w.WriteEndObject();
    }

    private static void WriteUi(Utf8JsonWriter w, UiProjectSettings s)
    {
        var d = new UiProjectSettings();
        if (s.ScaleMode == d.ScaleMode && s.ReferenceResolution == d.ReferenceResolution && s.MatchWidthOrHeight == d.MatchWidthOrHeight
            && s.MinScale == d.MinScale && s.MaxScale == d.MaxScale)
            return;
        w.WriteStartObject("ui");
        if (s.ScaleMode != d.ScaleMode)
            w.WriteString("scaleMode", s.ScaleMode.ToString());
        if (s.ReferenceResolution != d.ReferenceResolution)
            WriteFloats(w, "referenceResolution", [s.ReferenceResolution.X, s.ReferenceResolution.Y]);
        if (s.MatchWidthOrHeight != d.MatchWidthOrHeight)
            w.WriteNumber("matchWidthOrHeight", s.MatchWidthOrHeight);
        if (s.MinScale != d.MinScale)
            w.WriteNumber("minScale", s.MinScale);
        if (s.MaxScale != d.MaxScale)
            w.WriteNumber("maxScale", s.MaxScale);
        w.WriteEndObject();
    }

    private static void WriteAutoloads(Utf8JsonWriter w, List<AutoloadSettings> autoloads)
    {
        if (autoloads.Count == 0)
            return;
        w.WriteStartArray("autoloads");
        foreach (var a in autoloads)
        {
            w.WriteStartObject();
            w.WriteString("name", a.Name);
            if (a.Scene is not null)
                w.WriteString("scene", a.Scene);
            if (a.Type is not null)
                w.WriteString("type", a.Type);
            if (!a.Enabled)
                w.WriteBoolean("enabled", false);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WritePlayInstances(Utf8JsonWriter w, List<PlayInstanceSettings> instances)
    {
        if (instances.Count == 0)
            return;
        w.WriteStartArray("playInstances");
        foreach (var instance in instances)
        {
            w.WriteStartObject();
            w.WriteString("label", instance.Label);
            if (instance.Arguments.Count > 0)
                WriteStrings(w, "args", instance.Arguments);
            if (instance.DelaySeconds > 0)
                w.WriteNumber("delay", instance.DelaySeconds);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, List<string> values)
    {
        w.WriteStartArray(name);
        foreach (var value in values)
            w.WriteStringValue(value);
        w.WriteEndArray();
    }

    private static void WriteFloats(Utf8JsonWriter w, string name, ReadOnlySpan<float> values)
    {
        w.WriteStartArray(name);
        foreach (var value in values)
            w.WriteNumberValue(value);
        w.WriteEndArray();
    }

    // ----------------------------------------------------------------------------------------------------
    // Reading (unknown keys warn, wrong types throw with the key path)
    // ----------------------------------------------------------------------------------------------------

    private sealed class Reader(string source)
    {
        public ProjectSettings Read(JsonObject root)
        {
            var s = new ProjectSettings();
            Known(root, "", "format", "name", "engineVersion", "version", "mainScene", "assemblies", "isDemo", "steam", "window", "physics", "input",
                "audio", "localization", "rendering", "ui", "autoloads", "playInstances");
            if (String(root, "name", "name") is { } name)
                Guard("name", () => s.Name = name);
            s.EngineVersion = String(root, "engineVersion", "engineVersion") ?? s.EngineVersion;
            s.Version = String(root, "version", "version") ?? "";
            s.MainScene = String(root, "mainScene", "mainScene");
            s.Assemblies.AddRange(Strings(root, "assemblies", "assemblies"));
            s.IsDemo = Bool(root, "isDemo", "isDemo", s.IsDemo);
            if (Object(root, "steam") is { } steam)
                ReadSteam(steam, s.Steam);

            if (Object(root, "window") is { } window)
                ReadWindow(window, s.Window);
            if (Object(root, "physics") is { } physics)
                ReadPhysics(physics, s.Physics);
            if (Object(root, "input") is { } input)
                ReadInput(input, s.Input);
            if (Object(root, "audio") is { } audio)
                ReadAudio(audio, s.Audio);
            if (Object(root, "localization") is { } localization)
                ReadLocalization(localization, s.Localization);
            if (Object(root, "rendering") is { } rendering)
                ReadRendering(rendering, s.Rendering);
            if (Object(root, "ui") is { } ui)
                ReadUi(ui, s.Ui);
            if (Array(root, "autoloads", "autoloads") is { } autoloads)
                ReadAutoloads(autoloads, s.Autoloads);
            if (Array(root, "playInstances", "playInstances") is { } instances)
                ReadPlayInstances(instances, s.PlayInstances);
            return s;
        }

        private void ReadSteam(JsonObject o, SteamProjectSettings s)
        {
            Known(o, "steam.", "appId", "demoAppId", "devAppIdFile", "restartThroughSteam");
            s.AppId = (uint)Long(o, "appId", "steam.appId", s.AppId, 0, uint.MaxValue);
            s.DemoAppId = (uint)Long(o, "demoAppId", "steam.demoAppId", s.DemoAppId, 0, uint.MaxValue);
            s.DevAppIdFile = Bool(o, "devAppIdFile", "steam.devAppIdFile", s.DevAppIdFile);
            s.RestartThroughSteam = Bool(o, "restartThroughSteam", "steam.restartThroughSteam", s.RestartThroughSteam);
        }

        private void ReadWindow(JsonObject o, WindowSettings s)
        {
            Known(o, "window.", "title", "width", "height", "vsync", "maxFps", "icon", "stretchMode", "stretchAspect", "stretchScale", "stretchScaleMode", "contentScale");
            s.Title = String(o, "title", "window.title");
            s.Width = Int(o, "width", "window.width", s.Width, 1, 16384);
            s.Height = Int(o, "height", "window.height", s.Height, 1, 16384);
            s.VSync = Bool(o, "vsync", "window.vsync", s.VSync);
            s.MaxFps = Int(o, "maxFps", "window.maxFps", s.MaxFps, 0, 10000);
            s.Icon = String(o, "icon", "window.icon");
            s.StretchMode = EnumValue(o, "stretchMode", "window.stretchMode", s.StretchMode);
            s.StretchAspect = EnumValue(o, "stretchAspect", "window.stretchAspect", s.StretchAspect);
            var scale = Float(o, "stretchScale", "window.stretchScale", s.StretchScale);
            Guard("window.stretchScale", () => s.StretchScale = scale);
            s.StretchScaleMode = EnumValue(o, "stretchScaleMode", "window.stretchScaleMode", s.StretchScaleMode);
            var contentScale = Float(o, "contentScale", "window.contentScale", s.ContentScale);
            Guard("window.contentScale", () => s.ContentScale = contentScale);
        }

        // Enum names, case-insensitive; Godot's snake_case spellings ("canvas_items", "keep_width") are accepted too.
        private T EnumValue<T>(JsonObject o, string key, string where, T fallback) where T : struct, Enum
        {
            if (String(o, key, where) is not { } text)
                return fallback;
            var name = text.Replace("_", "", StringComparison.Ordinal);
            if (!Enum.TryParse<T>(name, ignoreCase: true, out var value) || !Enum.IsDefined(value) || char.IsDigit(name[0]))
                throw Error(where, $"'{text}' is not one of {string.Join(", ", Enum.GetNames<T>())}");
            return value;
        }

        private void ReadPhysics(JsonObject o, PhysicsProjectSettings s)
        {
            Known(o, "physics.", "ticksPerSecond", "maxStepsPerFrame", "3d", "2d");
            s.TicksPerSecond = Int(o, "ticksPerSecond", "physics.ticksPerSecond", s.TicksPerSecond, 1, 1000);
            s.MaxStepsPerFrame = Int(o, "maxStepsPerFrame", "physics.maxStepsPerFrame", s.MaxStepsPerFrame, 1, 100);
            if (Object(o, "3d") is { } p3)
            {
                Known(p3, "physics.3d.", "gravity", "substeps", "solverIterations", "relaxationIterations", "allowDeactivation", "multiThreaded",
                    "deterministic");
                var g = Floats(p3, "gravity", "physics.3d.gravity", 3);
                if (g is not null)
                    s.Physics3D.Gravity = new Vector3(g[0], g[1], g[2]);
                s.Physics3D.SubstepCount = Int(p3, "substeps", "physics.3d.substeps", s.Physics3D.SubstepCount, 1, 64);
                s.Physics3D.SolverIterations = Int(p3, "solverIterations", "physics.3d.solverIterations", s.Physics3D.SolverIterations, 1, 256);
                s.Physics3D.RelaxationIterations = Int(p3, "relaxationIterations", "physics.3d.relaxationIterations", s.Physics3D.RelaxationIterations, 0, 256);
                s.Physics3D.AllowDeactivation = Bool(p3, "allowDeactivation", "physics.3d.allowDeactivation", s.Physics3D.AllowDeactivation);
                s.Physics3D.MultiThreaded = Bool(p3, "multiThreaded", "physics.3d.multiThreaded", s.Physics3D.MultiThreaded);
                s.Physics3D.Deterministic = Bool(p3, "deterministic", "physics.3d.deterministic", s.Physics3D.Deterministic);
            }

            if (Object(o, "2d") is { } p2)
            {
                Known(p2, "physics.2d.", "gravity", "pixelsPerMeter", "substeps", "allowSleep", "continuous");
                var g = Floats(p2, "gravity", "physics.2d.gravity", 2);
                if (g is not null)
                    s.Physics2D.Gravity = new Vector2(g[0], g[1]);
                var ppm = Float(p2, "pixelsPerMeter", "physics.2d.pixelsPerMeter", s.Physics2D.PixelsPerMeter);
                Guard("physics.2d.pixelsPerMeter", () => s.Physics2D.PixelsPerMeter = ppm);
                s.Physics2D.SubstepCount = Int(p2, "substeps", "physics.2d.substeps", s.Physics2D.SubstepCount, 1, 64);
                s.Physics2D.AllowSleep = Bool(p2, "allowSleep", "physics.2d.allowSleep", s.Physics2D.AllowSleep);
                s.Physics2D.EnableContinuous = Bool(p2, "continuous", "physics.2d.continuous", s.Physics2D.EnableContinuous);
            }
        }

        private void ReadInput(JsonObject o, InputMap map)
        {
            foreach (var (name, node) in o)
            {
                var where = "input." + name;
                if (string.IsNullOrWhiteSpace(name))
                    throw Error("input", "action names must not be empty");
                if (node is not JsonObject action)
                    throw Error(where, "must be an object with \"bindings\"");
                Known(action, where + ".", "deadzone", "bindings");
                var created = map.AddAction(name, Float(action, "deadzone", where + ".deadzone", InputAction.DefaultDeadzone));
                foreach (var text in Strings(action, "bindings", where + ".bindings"))
                {
                    if (!InputBinding.TryParse(text, out var binding))
                        throw Error(where + ".bindings", $"'{text}' is not a binding (key:<Key>, mouse:<Button>, pad[N]:<Button>, axis[N]:<Axis>+/-)");
                    map.Bind(created.Name, binding);
                }
            }
        }

        private void ReadAudio(JsonObject o, AudioProjectSettings s)
        {
            Known(o, "audio.", "enabled", "busLayout", "sampleRate", "bufferMs");
            s.Enabled = Bool(o, "enabled", "audio.enabled", s.Enabled);
            if (o.ContainsKey("busLayout"))
                s.BusLayout = String(o, "busLayout", "audio.busLayout");
            s.SampleRate = Int(o, "sampleRate", "audio.sampleRate", s.SampleRate, 8000, 384000);
            s.BufferMilliseconds = Int(o, "bufferMs", "audio.bufferMs", s.BufferMilliseconds, 1, 1000);
        }

        private void ReadLocalization(JsonObject o, LocalizationProjectSettings s)
        {
            Known(o, "localization.", "defaultLocale", "sourceLocale", "fallbacks", "directory", "domain");
            s.DefaultLocale = String(o, "defaultLocale", "localization.defaultLocale");
            s.SourceLocale = String(o, "sourceLocale", "localization.sourceLocale") ?? s.SourceLocale;
            s.Fallbacks.AddRange(Strings(o, "fallbacks", "localization.fallbacks"));
            s.LocaleDirectory = String(o, "directory", "localization.directory") ?? s.LocaleDirectory;
            s.Domain = String(o, "domain", "localization.domain") ?? s.Domain;
        }

        private void ReadRendering(JsonObject o, RenderingProjectSettings s)
        {
            Known(o, "rendering.", "exposure", "shadows", "canvasClearColor", "antiAliasing", "taaSharpness", "waterSsr",
                "scaling3DMode", "scaling3DScale", "fsrSharpness");
            s.AntiAliasing = EnumValue(o, "antiAliasing", "rendering.antiAliasing", s.AntiAliasing);
            s.WaterSsr = EnumValue(o, "waterSsr", "rendering.waterSsr", s.WaterSsr);
            var taaSharpness = Float(o, "taaSharpness", "rendering.taaSharpness", s.TaaSharpness);
            Guard("rendering.taaSharpness", () => s.TaaSharpness = taaSharpness);
            s.Scaling3DMode = EnumValue(o, "scaling3DMode", "rendering.scaling3DMode", s.Scaling3DMode);
            var scale = Float(o, "scaling3DScale", "rendering.scaling3DScale", s.Scaling3DScale);
            Guard("rendering.scaling3DScale", () => s.Scaling3DScale = scale);
            var fsrSharpness = Float(o, "fsrSharpness", "rendering.fsrSharpness", s.FsrSharpness);
            Guard("rendering.fsrSharpness", () => s.FsrSharpness = fsrSharpness);
            if (Floats(o, "canvasClearColor", "rendering.canvasClearColor", 4) is { } clear)
                s.CanvasClearColor = new System.Numerics.Vector4(clear[0], clear[1], clear[2], clear[3]);
            var exposure = Float(o, "exposure", "rendering.exposure", s.Exposure);
            Guard("rendering.exposure", () => s.Exposure = exposure);
            if (String(o, "shadows", "rendering.shadows") is { } shadows)
            {
                if (!Enum.TryParse<ShadowQuality>(shadows, ignoreCase: true, out var quality) || !Enum.IsDefined(quality) || char.IsDigit(shadows[0]))
                    throw Error("rendering.shadows", $"'{shadows}' is not one of {string.Join(", ", Enum.GetNames<ShadowQuality>())}");
                s.Shadows = quality;
            }
        }

        private void ReadUi(JsonObject o, UiProjectSettings s)
        {
            Known(o, "ui.", "scaleMode", "referenceResolution", "matchWidthOrHeight", "minScale", "maxScale");
            s.ScaleMode = EnumValue(o, "scaleMode", "ui.scaleMode", s.ScaleMode);
            if (Floats(o, "referenceResolution", "ui.referenceResolution", 2) is { } reference)
            {
                if (reference[0] != MathF.Round(reference[0]) || reference[1] != MathF.Round(reference[1]))
                    throw Error("ui.referenceResolution", "must be whole pixels");
                Guard("ui.referenceResolution", () => s.ReferenceResolution = new Vector2I((int)reference[0], (int)reference[1]));
            }

            var match = Float(o, "matchWidthOrHeight", "ui.matchWidthOrHeight", s.MatchWidthOrHeight);
            Guard("ui.matchWidthOrHeight", () => s.MatchWidthOrHeight = match);
            var max = Float(o, "maxScale", "ui.maxScale", s.MaxScale);
            Guard("ui.maxScale", () => s.MaxScale = max);
            var min = Float(o, "minScale", "ui.minScale", s.MinScale);
            Guard("ui.minScale", () => s.MinScale = min);
        }

        private void ReadAutoloads(JsonArray array, List<AutoloadSettings> autoloads)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < array.Count; i++)
            {
                var where = $"autoloads[{i.ToString(CultureInfo.InvariantCulture)}]";
                if (array[i] is not JsonObject o)
                    throw Error(where, "must be an object");
                Known(o, where + ".", "name", "scene", "type", "enabled");
                var name = String(o, "name", where + ".name");
                if (string.IsNullOrWhiteSpace(name) || name.Contains('/', StringComparison.Ordinal))
                    throw Error(where + ".name", "must be a node name (no '/')");
                if (!names.Add(name))
                    throw Error(where + ".name", $"'{name}' is used by another autoload");
                var scene = String(o, "scene", where + ".scene");
                var type = String(o, "type", where + ".type");
                if ((scene is null) == (type is null))
                    throw Error(where, "needs exactly one of \"scene\" or \"type\"");
                autoloads.Add(new AutoloadSettings { Name = name, Scene = scene, Type = type, Enabled = Bool(o, "enabled", where + ".enabled", true) });
            }
        }

        private void ReadPlayInstances(JsonArray array, List<PlayInstanceSettings> instances)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var where = $"playInstances[{i.ToString(CultureInfo.InvariantCulture)}]";
                if (array[i] is not JsonObject o)
                    throw Error(where, "must be an object");
                Known(o, where + ".", "label", "args", "delay");
                var label = String(o, "label", where + ".label");
                if (string.IsNullOrWhiteSpace(label))
                    throw Error(where + ".label", "must not be empty");
                var instance = new PlayInstanceSettings { Label = label, DelaySeconds = Math.Clamp(Float(o, "delay", where + ".delay", 0f), 0f, 600f) };
                instance.Arguments.AddRange(Strings(o, "args", where + ".args"));
                instances.Add(instance);
            }
        }

        // --- typed accessors ------------------------------------------------------------------------------

        private void Known(JsonObject o, string prefix, params ReadOnlySpan<string> keys)
        {
            foreach (var (key, _) in o)
            {
                var known = false;
                foreach (var k in keys)
                {
                    if (string.Equals(k, key, StringComparison.Ordinal))
                    {
                        known = true;
                        break;
                    }
                }

                if (!known)
                    Log.Warning($"[Project] '{source}': unknown setting '{prefix}{key}' ignored.");
            }
        }

        private InvalidDataException Error(string where, string message) => new($"'{source}': {where} {message}.");

        // Runs a validating setter; its ArgumentException becomes a file error naming the setting.
        private void Guard(string where, Action assign)
        {
            try
            {
                assign();
            }
            catch (ArgumentException e)
            {
                throw new InvalidDataException($"'{source}': {where} is invalid: {e.Message}", e);
            }
        }

        private JsonObject? Object(JsonObject o, string key) =>
            o[key] switch
            {
                null => null,
                JsonObject value => value,
                _ => throw Error(key, "must be an object"),
            };

        private JsonArray? Array(JsonObject o, string key, string where) =>
            o[key] switch
            {
                null => null,
                JsonArray value => value,
                _ => throw Error(where, "must be an array"),
            };

        public string? String(JsonObject o, string key, string where)
        {
            var node = o[key];
            if (node is null)
                return null;
            if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                return v.GetValue<string>();
            throw Error(where, "must be a string");
        }

        private List<string> Strings(JsonObject o, string key, string where)
        {
            var result = new List<string>();
            if (Array(o, key, where) is not { } array)
                return result;
            foreach (var item in array)
            {
                if (item is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                    result.Add(v.GetValue<string>());
                else
                    throw Error(where, "must contain only strings");
            }

            return result;
        }

        public int Int(JsonObject o, string key, string where, int fallback, int min = int.MinValue, int max = int.MaxValue) =>
            (int)Long(o, key, where, fallback, min, max);

        private long Long(JsonObject o, string key, string where, long fallback, long min, long max)
        {
            var node = o[key];
            if (node is null)
                return fallback;
            if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<long>(out var value))
            {
                if (value < min || value > max)
                    throw Error(where, $"must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)} (got {value.ToString(CultureInfo.InvariantCulture)})");
                return value;
            }

            throw Error(where, "must be an integer");
        }

        private float Float(JsonObject o, string key, string where, float fallback)
        {
            var node = o[key];
            if (node is null)
                return fallback;
            if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var value) && double.IsFinite(value))
                return (float)value;
            throw Error(where, "must be a finite number");
        }

        private bool Bool(JsonObject o, string key, string where, bool fallback)
        {
            var node = o[key];
            if (node is null)
                return fallback;
            if (node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                return v.GetValue<bool>();
            throw Error(where, "must be true or false");
        }

        private float[]? Floats(JsonObject o, string key, string where, int count)
        {
            if (Array(o, key, where) is not { } array)
                return null;
            if (array.Count != count)
                throw Error(where, $"must have {count.ToString(CultureInfo.InvariantCulture)} numbers");
            var result = new float[count];
            for (var i = 0; i < count; i++)
            {
                if (array[i] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var value) && double.IsFinite(value))
                    result[i] = (float)value;
                else
                    throw Error(where, "must contain only finite numbers");
            }

            return result;
        }
    }
}
