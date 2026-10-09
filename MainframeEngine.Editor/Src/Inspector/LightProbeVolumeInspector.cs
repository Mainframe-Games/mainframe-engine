using System.Globalization;
using System.Text;
using MainframeEngine.Serialization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The <see cref="LightProbeVolume"/> inspector (ADR 0170): above the generated rows, the bake's state (none, current,
/// stale, baking) and a Bake Lighting button. The bake runs in the background on every core (the editor stays live); when
/// it finishes the data is saved next to the scene (<c>&lt;scene&gt;-lighting.mres</c> and its <c>.probes</c> file, or
/// over the volume's existing data file) and <see cref="LightProbeVolume.Data"/> is set as one undoable edit.
/// </summary>
[CustomInspector(typeof(LightProbeVolume))]
public sealed class LightProbeVolumeInspector : ICustomInspector, IWorkspaceInspector
{
    public EditorWorkspace? Workspace { get; set; }

    public string? GetHeaderRml(object target)
    {
        var volume = (LightProbeVolume)target;
        var rml = new StringBuilder(512);
        rml.Append("<div class=\"probe-bake\">");
        if (volume.IsBaking)
        {
            rml.Append("<button class=\"tool-button\" data-action=\"probes-cancel\" data-tooltip=\"Cancel the bake\">")
                .Append("<span class=\"icon icon-sm icon-player-stop\"></span></button>");
        }
        else
        {
            rml.Append("<button class=\"tool-button\" data-action=\"probes-bake\" data-tooltip=\"Bake Lighting — trace the sky ")
                .Append("occlusion and bounce light of this volume's probes (every core, in the background) and save them next to the scene\">")
                .Append("<span class=\"icon icon-sm icon-bulb\"></span></button>");
        }

        rml.Append("<span class=\"probe-bake-status\">").Append(RmlText.Escape(Status(volume))).Append("</span></div>");
        return rml.ToString();
    }

    /// <summary>One line on the volume's bake: baking, none, current or stale, with the probe count.</summary>
    internal static string Status(LightProbeVolume volume)
    {
        if (volume.IsBaking)
            return "Baking…";
        if (volume.LastBakeError is { } error)
            return $"The last bake failed: {error.Split('\n')[0]}";
        if (volume.Data is not { HasData: true } data)
            return "Not baked: the world renders with sky light only.";
        var grid = data.Grid;
        var probes = string.Create(CultureInfo.InvariantCulture, $"{grid.ProbeCount:N0} probes");
        return volume.IsBakeCurrent() ? $"Baked: {probes}." : $"Stale: {probes} baked for a different scene. Bake again.";
    }

    public void OnAction(object target, string action, IInspectorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var volume = (LightProbeVolume)target;
        switch (action)
        {
            case "probes-bake":
                StartBake(volume, context);
                break;
            case "probes-cancel":
                volume.CancelBake();
                Workspace?.Inspector.Rebuild();
                break;
        }
    }

    public void RefreshPreview(object target, RmlDocument document)
    {
    }

    private void StartBake(LightProbeVolume volume, IInspectorContext context)
    {
        var previous = volume.Data;
        var scenePath = (context as EditedScene)?.FilePath;
        void OnBaked()
        {
            volume.Baked -= OnBaked;
            Finish(volume, previous, scenePath, context);
        }

        volume.Baked += OnBaked;
        volume.BakeInBackground();
        Workspace?.Inspector.Rebuild();
    }

    // Saves the new data over the old file, else next to the scene, and records the change of Data as one undo entry.
    private void Finish(LightProbeVolume volume, LightProbeData? previous, string? scenePath, IInspectorContext context)
    {
        if (volume.Data is not { } data)
            return;
        var target = previous?.ResourcePath ?? TargetPath(scenePath);
        if (target is not null)
        {
            try
            {
                data.Save(target);
                Log.Info($"[Editor] Saved the light probes to {target}.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error($"[Editor] Could not save the light probes to {target}: {e.Message}");
            }
        }
        else
        {
            Log.Warning("[Editor] The scene has no file yet: the light probes stay in memory until it is saved and baked again.");
        }

        var property = TypeRegistry.GetRequired(typeof(LightProbeVolume)).FindProperty(nameof(LightProbeVolume.Data))
                       ?? throw new InvalidOperationException("LightProbeVolume.Data is not exported.");
        context.History.Commit(new SetPropertyAction(volume, property, previous, data));
        Workspace?.Inspector.Rebuild();
    }

    /// <summary><c>&lt;scene folder&gt;/&lt;scene name&gt;-lighting.mres</c> as a project path (null for an unsaved scene).</summary>
    internal static string? TargetPath(string? scenePath)
    {
        if (string.IsNullOrEmpty(scenePath))
            return null;
        var full = AssetDatabase.Current.ToAbsolutePath(scenePath);
        var file = Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + "-lighting.mres");
        return AssetDatabase.Current.ToProjectPath(file);
    }
}
