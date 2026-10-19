using System.Globalization;
using System.Numerics;
using MainframeEngine.Audio.Music;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// The Sections lane's menus and dialogs (the <see cref="SongView"/> hooks them to each tab's controller): a section's
/// right-click menu (rename, loop, then, kind, start, a marker, delete), a marker's (rename, exit, tail point, delete), an
/// empty spot's (a new section or marker there), and the rename dialogs. Every choice is one <see cref="SongDocument"/> step.
/// </summary>
public static class SongSections
{
    /// <summary>Hooks <paramref name="tab"/>'s Sections lane to the workspace's menus and dialogs.</summary>
    public static void Attach(EditorWorkspace workspace, SongTab tab)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(tab);
        var controller = tab.Controller;
        controller.RequestRenameSection = section => Rename(workspace, tab, "Rename Section", section.Name, name => tab.Document.RenameSection(section, name),
            () => tab.Document.Song.Adaptive?.Sections.Contains(section) == true);
        controller.RequestRenameMarker = marker => Rename(workspace, tab, "Rename Marker", marker.Name, name => tab.Document.RenameMarker(marker, name),
            () => tab.Document.Song.Adaptive?.Markers.Contains(marker) == true);
        controller.RequestSectionMenu = (section, marker, tick, local) => Menu(workspace, tab, section, marker, tick, local);
        tab.Curves.RequestMenu = (curve, local) => CurveMenu(workspace, tab, curve, local);
    }

    // The Curves pane's right-click: the curve's shape (the whole curve has one), or remove it.
    private static void CurveMenu(EditorWorkspace workspace, SongTab tab, MusicCurveInfo curve, Vector2 local)
    {
        var doc = tab.Document;
        var editable = !doc.IsReadOnly;
        List<MenuItem> items =
        [
            MenuItem.Header("Curve shape"),
            new("Linear", "linear", Enabled: editable, Css: curve.Shape == MusicCurveShape.Linear ? "current" : null),
            new("Smooth", "smooth", Enabled: editable, Css: curve.Shape == MusicCurveShape.Smooth ? "current" : null),
            MenuItem.Separator,
            new("Remove Curve", "remove", Enabled: editable, Icon: "trash"),
        ];
        var rect = workspace.SongPanel.RollRect;
        workspace.Popup.Show(items, rect.X + local.X, rect.Y + local.Y, command =>
        {
            if (command is null || doc.IsReadOnly || doc.Song.Adaptive?.Curves.Contains(curve) != true)
                return;
            Guard(workspace, () =>
            {
                if (command == "remove")
                {
                    doc.RemoveCurve(curve);
                    tab.SelectCurve(null);
                }
                else
                {
                    doc.SetCurveShape(curve, command == "smooth" ? MusicCurveShape.Smooth : MusicCurveShape.Linear);
                }
            });
        });
    }

    private static void Rename(EditorWorkspace workspace, SongTab tab, string title, string current, Action<string> rename, Func<bool> exists) =>
        workspace.Message.Show(new MessageRequest
        {
            Title = title,
            Message = "The new name:",
            Input = current,
            Buttons = ["Rename", "Cancel"],
            DefaultButton = 0,
            CancelButton = 1,
            Callback = (button, text) =>
            {
                if (button == 0 && !string.IsNullOrWhiteSpace(text) && exists() && !tab.Document.IsReadOnly)
                    Guard(workspace, () => rename(text.Trim()));
            },
        });

    private static void Menu(EditorWorkspace workspace, SongTab tab, SongSection? section, SongMarker? marker, long tick, Vector2 local)
    {
        if (tab.Document.Song.Adaptive is not { } adaptive)
            return;
        var doc = tab.Document;
        var editable = !doc.IsReadOnly;
        var song = doc.Song;
        var items = new List<MenuItem>();
        if (marker is not null)
        {
            items.Add(MenuItem.Header("Marker " + marker.Name));
            items.Add(new MenuItem("Rename…", "rename", Enabled: editable, Icon: "pencil"));
            items.Add(new MenuItem("Exit Point", "exit", Enabled: editable, Css: marker.Exit ? "current" : null, Icon: "arrow-right"));
            items.Add(new MenuItem("Tail Point", "tail", Enabled: editable && marker.Exit, Css: marker.Tail ? "current" : null, Icon: "wave-sine"));
            items.Add(MenuItem.Separator);
            items.Add(new MenuItem("Delete Marker", "delete", Enabled: editable, Icon: "trash"));
        }
        else if (section is not null)
        {
            items.Add(MenuItem.Header(section.Name));
            items.Add(new MenuItem("Rename…", "rename", Enabled: editable, Icon: "pencil"));
            if (section.Kind == MusicSectionKind.Section)
            {
                items.Add(new MenuItem("Loop", "loop", Enabled: editable, Css: section.Loop ? "current" : null, Icon: "repeat"));
                items.Add(new MenuItem("Set as Start", "start", Enabled: editable, Css: adaptive.Start == section.Id ? "current" : null, Icon: "player-play"));
                items.Add(MenuItem.Header("Then (instead of looping)"));
                items.Add(new MenuItem("End the music", "then:", Enabled: editable, Css: !section.Loop && section.Then is null ? "current" : null));
                foreach (var other in adaptive.Sections)
                {
                    if (other.Kind == MusicSectionKind.Section && !ReferenceEquals(other, section))
                        items.Add(new MenuItem(other.Name, "then:" + other.Id, Enabled: editable, Css: !section.Loop && section.Then == other.Id ? "current" : null));
                }
            }

            items.Add(MenuItem.Header("Kind"));
            foreach (var (kind, label) in (ReadOnlySpan<(MusicSectionKind, string)>)[(MusicSectionKind.Section, "Section"), (MusicSectionKind.Transition, "Transition Segment"), (MusicSectionKind.Stinger, "Stinger")])
                items.Add(new MenuItem(label, "kind:" + ((int)kind).ToString(CultureInfo.InvariantCulture), Enabled: editable, Css: section.Kind == kind ? "current" : null));
            items.Add(MenuItem.Separator);
            items.Add(new MenuItem("Add Marker Here", "marker", Enabled: editable, Icon: "plus"));
            items.Add(new MenuItem("Delete Section", "delete", Enabled: editable, Icon: "trash"));
        }
        else
        {
            items.Add(new MenuItem("New Section Here", "new", Enabled: editable, Icon: "plus"));
            items.Add(new MenuItem("Add Marker Here", "marker", Enabled: editable, Icon: "plus"));
        }

        var rect = workspace.SongPanel.ArrangeRect;
        workspace.Popup.Show(items, rect.X + local.X, rect.Y + local.Y, command =>
        {
            if (command is null || doc.IsReadOnly || doc.Song.Adaptive is null)
                return;
            Guard(workspace, () =>
            {
                var beat = (long)Math.Round(SongAdaptiveCompiler.TicksPerBeat(song));
                switch (command)
                {
                    case "rename" when marker is not null:
                        tab.Controller.RequestRenameMarker?.Invoke(marker);
                        break;
                    case "rename" when section is not null:
                        tab.Controller.RequestRenameSection?.Invoke(section);
                        break;
                    case "exit" when marker is not null:
                        doc.SetMarkerExit(marker, !marker.Exit);
                        break;
                    case "tail" when marker is not null:
                        doc.SetMarkerTail(marker, !marker.Tail);
                        break;
                    case "delete" when marker is not null:
                        doc.RemoveMarker(marker);
                        break;
                    case "delete" when section is not null:
                        doc.RemoveSection(section);
                        break;
                    case "loop" when section is not null:
                        doc.SetSectionLoop(section, !section.Loop);
                        break;
                    case "start" when section is not null:
                        doc.SetStartSection(section.Id);
                        break;
                    case "marker":
                        var at = Math.Max(0, SongGrid.Round(tick, Math.Max(1, beat)));
                        doc.AddMarker("Marker " + (doc.Song.Adaptive!.Markers.Count + 1).ToString(CultureInfo.InvariantCulture), at);
                        break;
                    case "new":
                        var bar = song.TicksPerBar;
                        var start = SongGrid.Floor(tick, bar);
                        if (doc.Song.Adaptive!.Sections.Exists(s => s.Start < start + bar && s.End > start))
                            return;
                        var created = doc.AddSection("Section " + (doc.Song.Adaptive.Sections.Count + 1).ToString(CultureInfo.InvariantCulture),
                            MusicSectionKind.Section, start, start + bar);
                        tab.SelectSection(created);
                        break;
                    default:
                        if (section is null)
                            break;
                        if (command.StartsWith("then:", StringComparison.Ordinal))
                        {
                            if (section.Loop)
                                doc.SetSectionLoop(section, false); // a continuation replaces the loop
                            doc.SetSectionThen(section, command.Length > 5 ? command[5..] : null);
                        }
                        else if (command.StartsWith("kind:", StringComparison.Ordinal) && int.TryParse(command.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var kind))
                            doc.SetSectionKind(section, (MusicSectionKind)kind);
                        break;
                }
            });
        });
    }

    private static void Guard(EditorWorkspace workspace, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            workspace.Commands.ReportError("Section edit failed", e);
        }
    }
}
