# Video Editing Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Mainframe Editor a video editing mode. A Game|Video toggle switches the whole editor UI to a
Cutback-style video editor that trims gameplay recordings, removes silence, idle gameplay and filler words, and
exports, all inside the editor.

**Architecture:**
- **Library.** Cutback's logic is copied into a new editor-only library, `MainframeEngine.Video`: Core, Media and
  Analysis, all MIT, from Cutback commit `90cc508`.
- **Modes.** The editor keeps one workspace and gains a mode. Switching mode swaps the layout, menus, toolbar and tab
  filter.
- **Video mode.** Each recording or `.mfcut` project opens as a `VideoTab` in its own panel, made of:
  - an RmlUi chrome around a `Node2D` timeline canvas;
  - a preview texture decoded by ffmpeg, timed by an `AudioStreamGenerator` voice that acts as the clock;
  - an RmlUi transcript panel.

**Tech Stack:** .NET 10; the engine's editor (`EditorDocument`/RmlUi, `SubViewport` + `UiServer.RegisterTexture`,
`CanvasItem` drawing, `AudioStreamGenerator`, `EditorSession`/`EditorCommands`); Whisper.net 1.9.1; xunit.v3 with plain
`Assert`.

**Spec:** `docs/design/future/video-editor.md` (this branch), with its four SVGs in `docs/images/video-editor-*.svg`.

**Repository and branch:** every path is relative to the worktree
`/Users/brogan/mainframe/mainframe-engine/.claude/worktrees/video-editor`, branch `feature/video-editor`. The branch is
cut from `main`, which already has #62 (the recorder) and #63 (recording from the editor). The PR targets `main`.

**Port source:** Cutback at commit `90cc508` (branch `feature/phase2-gameplay-cuts`) in `/Users/brogan/dev/SimpleVideoEditor`.
Extract it with:

```bash
mkdir -p /tmp/cutback-src && git -C /Users/brogan/dev/SimpleVideoEditor archive 90cc508 src/Cutback.Core src/Cutback.Media src/Cutback.Analysis tests | tar -x -C /tmp/cutback-src
```

At that commit Cutback has 282 Core tests, 83 Media tests and 27 Analysis tests. Every one is ported.

## Global Constraints

- **Licence:** the copied code is MIT. The author relicensed Analysis and the ported app logic on 2026-10-13 (spec,
  Licensing). Keep the line `Copyright (c) 2026 Cutback contributors` in `MainframeEngine.Video/LICENSE-CUTBACK`.
- **`MainframeEngine.Video` stays pure:**
  - its only dependencies are System.Text.Json, Whisper.net and Whisper.net.Runtime 1.9.1;
  - it never references `MainframeEngine` (the runtime) or the editor;
  - only `MainframeEngine.Editor` and its tests reference it, and the runtime never does.
- **Tests never shell out to ffmpeg and never load a Whisper model.** This is Cutback's rule, kept.
- **Namespaces:**

  | Cutback | Engine |
  |---|---|
  | `Cutback.Core` | `MainframeEngine.Video` |
  | `Cutback.Core.X` | `MainframeEngine.Video.X` |
  | `Cutback.Media` | `MainframeEngine.Video.Media` |
  | `Cutback.Media.Export` | `MainframeEngine.Video.Media.Export` |
  | `Cutback.Analysis` | `MainframeEngine.Video.Analysis` |
  | (editor UI) | `MainframeEngine.Editor.Video` |
- **Renames on the way in:**
  - `CutbackProject` becomes `VideoProject`.
  - `FfmpegNotFoundException` becomes `FfmpegMissingException`. The engine already has a public
    `MainframeEngine.FfmpegNotFoundException`, and C# would bind the bare name to the engine's type in editor code.
  - `TempSession`'s folder `Cutback` becomes `MainframeVideo`.
  - `ModelStore.CreateDefault()` is replaced by a constructor that takes the editor's user-data path.
- **Name hazard in tests:** the engine's `internal` `CaptureSpan` and `CaptureMarker` (namespace `MainframeEngine`) are
  visible to `MainframeEngine.Editor.Tests`. In editor tests, refer to the video ones through
  `using VideoCapture = MainframeEngine.Video.Capture;` or the fully qualified name.
- **Project file:** `.mfcut` (`ProjectSerializer.FileExtension`). A `.cutback` file opens (`LegacyFileExtension`) and is
  saved as `.mfcut`. The schema is unchanged (version 3).
- **Ported constants, never retuned:**
  - padding 60 ms, min silence 400 ms, threshold −34 dB, min keep 120 ms;
  - gameplay detection 3000/500/1000/1500 ms;
  - fade 8 ms;
  - boundary snap 20 ms, word snap 40 ms;
  - timeline: ruler 22 dp, grab 6 dp, drag threshold 4 dp, minimum view 0.25 s, wheel zoom 1.25, label spacing 80 dp;
  - preview: at most 960 px wide and 30 fps, 1.0 s prefetch, 1.0 s audio ring.
- **Engine conventions:**
  - file-scoped namespaces; `sealed` by default; dense `<summary>` docs that say why;
  - `AnalysisLevel=latest-recommended`, with warnings as errors;
  - tests in plain xunit `Assert`, with PascalCase sentence names;
  - editor tests that touch global state use `[Collection(nameof(SerialEditor))]`.
- **New editor UI:**
  - RML goes in `MainframeEngine.Editor/Assets/Editor/`, using `theme.rcss` classes.
  - Every menu item has an `Icon` that exists in `Assets/icons/icons.txt` (`IconTests` enforces this).
  - Hidden documents that hold text fields are hidden with `HideAndReleaseFocus()`.
- **Gates for each task:** `just build`, the task's focused `dotnet test … --filter`, and `just format-check`. Before the
  final review, also run:
  - `just test` (Task 1 adds the Video tests to it);
  - `dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false`;
  - `just test-render`.
- **Never push**; the user decides. Commit trailer: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **The mode switches while a video plays or a job runs.**
   - Expected: playback pauses; jobs keep running and their buttons show progress again on return; switching back
     restores the same tab and view.
   - Tests: Task 4 `SwitchingModesKeepsEachModesActiveTab`; Task 10 `LeavingVideoModePausesPlayback`.
2. **The tab strip filters by mode, and its indices are the filtered ones.**
   - Expected: a close or activate hits the clicked tab, not the session tab with the same index.
   - Test: Task 4 `ClosingAFilteredTabClosesThatTab`.
3. **Editing while playing, or closing a tab with decoders running.**
   - Expected: an edit rebuilds the playback plan from the playhead; closing kills every decoder and the voice, and
     leaves no ffmpeg running.
   - Tests: Task 9 `ASegmentEditRebuildsThePlanFromThePlayhead`, `DisposeKillsEveryDecoder`.
4. **Bad input files.**
   - Expected:
     - a recording without a sidecar opens, with Detect Idle disabled;
     - a bad sidecar opens with a warning;
     - a video without audio is refused;
     - a project whose source is missing is refused, naming the path;
     - a changed source opens with a warning;
     - a `.cutback` opens as `<name>.mfcut` and is never overwritten.
   - Tests: Task 5 `ABadSidecarOpensWithAWarning`, `AProjectWhoseSourceIsMissingIsRefused`,
     `AChangedSourceOpensWithAWarning`, `ALegacyProjectSavesAsMfcutNextToIt`.
5. **Transcript edits interleaved with timeline edits and undo.**
   - Expected: a word click is one undo step and marks its range manual; Remove Filler Words never cuts a manual range;
     undo restores the struck state.
   - Tests: Task 5 `CuttingWordsIsOneManualUndoStep`, `FillerRemovalLeavesManualEditsAlone`; Task 12
     `ClickingAStruckWordRestoresIt`.

---

### Task 1: `MainframeEngine.Video` — the project, Core ported, `SegmentEditor`, `.mfcut`

**Files:**
- Create: `MainframeEngine.Video/MainframeEngine.Video.csproj` and `MainframeEngine.Video/LICENSE-CUTBACK`.
- Create: `MainframeEngine.Video/**`, every file of `src/Cutback.Core/`. Root files go at the root; `Models/`,
  `Capture/`, `Detection/`, `Transcript/`, `Projects/` and `Playback/` keep their folders.
- Create: `MainframeEngine.Video/SegmentEditor.cs` (new).
- Create: `Tests/MainframeEngine.Video.Tests/MainframeEngine.Video.Tests.csproj`, every file of `tests/Cutback.Core.Tests/`
  (with `Fixtures/sample.mfcap.json` byte-identical), and `SegmentEditorTests.cs` (new).
- Modify: `MainframeEngine.slnx` (both projects) and the `justfile` `test` recipe. Also `.github/workflows/ci.yml:154`
  and `build/linux/inside.sh:43`, so the new test project runs beside the editor tests.

**Interfaces:**
- Produces, for later tasks:
  - `MainframeEngine.Video`: `SegmentList`, `EditHistory<T>`, `InvalidPartitionException`.
  - `MainframeEngine.Video.Models`: `Segment`, `SegmentOrigin`, `SourceInfo`, `Word`, `DetectionSettings`,
    `GameplaySettings`, `VideoProject`.
  - `….Capture`: `CaptureSidecar`, `CaptureSpan`, `CaptureMarker`, `CaptureMode`, `CaptureSidecarSerializer`,
    `CaptureSidecarFormatException`.
  - `….Detection`: `SilenceSpan`, `PlannedCut`, `SilenceCutPlanner`, `GameplayCutPlanner`, `FillerDetector`,
    `FillerSpan`, `FillerSpanLocator`, `FillerCutPlanner`.
  - `….Transcript`: `TranscriptView`, `OutputTimeline`, `TranscriptExporter`.
  - `….Projects`: `ProjectSerializer`, `ProjectMigrator`, `ProjectFormatException`, `SourceHash`.
  - `….Playback`: `PlayheadInterpolator`.

  All keep Cutback's public signatures, apart from the `VideoProject` rename. These are new:
  ```csharp
  // MainframeEngine.Video.Projects.ProjectSerializer
  public const string FileExtension = ".mfcut";
  public const string LegacyFileExtension = ".cutback";
  public static bool IsProjectPath(string path);      // .mfcut or .cutback, case-insensitive
  // MainframeEngine.Video.SegmentEditor — exact API below
  ```

- [ ] **Step 1: The projects**

`MainframeEngine.Video/MainframeEngine.Video.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    The video editor's logic (editor-only; games never reference it): Cutback's Core, Media and Analysis, copied in
    from Cutback 90cc508 under MIT (LICENSE-CUTBACK). Pure .NET plus the user's ffmpeg as a child process; no engine
    or UI types. See docs/design/future/video-editor.md.
  -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>MainframeEngine.Video</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="MainframeEngine.Video.Tests" />
  </ItemGroup>

  <ItemGroup>
    <None Include="LICENSE-CUTBACK" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

`MainframeEngine.Video/LICENSE-CUTBACK` is the text of `/tmp/cutback-src/src/Cutback.Core/LICENSE`, preceded by:

```
The files in MainframeEngine.Video were copied from Cutback (https://github.com/brogan89/Cutback) at commit 90cc508
and are licensed under the MIT licence below. Cutback.Analysis and the ported parts of Cutback.App were relicensed
from GPL-3.0 to MIT for this copy by their sole author on 2026-10-13.

```

`Tests/MainframeEngine.Video.Tests/MainframeEngine.Video.Tests.csproj` copies `MainframeEngine.Editor.Tests.csproj`:
- the same first `PropertyGroup` and package references;
- `<Using Include="Xunit" />`;
- one `ProjectReference`, to `..\..\MainframeEngine.Video\MainframeEngine.Video.csproj`;
- `<None Include="Fixtures\**" CopyToOutputDirectory="PreserveNewest" />`.

Add both projects to `MainframeEngine.slnx`: the library at the root next to `MainframeEngine.Editor`, and the tests
in `/Tests/`. Add `dotnet test Tests/MainframeEngine.Video.Tests` to the `justfile` `test` recipe. Add the matching line
to `.github/workflows/ci.yml` (copy line 154 with the new project), and copy line 43's `run_tests` call in
`build/linux/inside.sh` the same way.

- [ ] **Step 2: Copy Core and rewrite the namespaces**

```bash
cp -R /tmp/cutback-src/src/Cutback.Core/. MainframeEngine.Video/
rm -f MainframeEngine.Video/Cutback.Core.csproj MainframeEngine.Video/LICENSE MainframeEngine.Video/README.md
cp -R /tmp/cutback-src/tests/Cutback.Core.Tests/. Tests/MainframeEngine.Video.Tests/
rm -f Tests/MainframeEngine.Video.Tests/Cutback.Core.Tests.csproj
grep -rl "Cutback\.Core" MainframeEngine.Video Tests/MainframeEngine.Video.Tests --include='*.cs' | xargs sed -i '' -e 's/\bCutback\.Core\b/MainframeEngine.Video/g'
grep -rl "CutbackProject" MainframeEngine.Video Tests/MainframeEngine.Video.Tests --include='*.cs' | xargs sed -i '' -e 's/\bCutbackProject\b/VideoProject/g'
mv MainframeEngine.Video/Models/CutbackProject.cs MainframeEngine.Video/Models/VideoProject.cs
```

Then:
- In comments only, change "the Cutback app" to "the video editor". Leave comments that explain history alone; they are
  still true about where the code came from.
- In `ProjectSerializer`:
  - `FileExtension` becomes `".mfcut"`;
  - add `LegacyFileExtension = ".cutback"`;
  - add `IsProjectPath(path)`, which compares `Path.GetExtension(path)` with either extension, ignoring case.

  Add this test:
  ```csharp
  [Theory]
  [InlineData("/r/a.mfcut", true)]
  [InlineData("/r/a.CUTBACK", true)]
  [InlineData("/r/a.mp4", false)]
  public void IsProjectPathAcceptsBothProjectExtensions(string path, bool expected) =>
      Assert.Equal(expected, ProjectSerializer.IsProjectPath(path));
  ```
- `ProjectFormatException` messages that say "Cutback" now say "This project".

- [ ] **Step 3: Convert the tests to plain `Assert`**

Convert every `tests/Cutback.Core.Tests` file:
- Remove `using FluentAssertions;`.
- Rename methods from snake_case to PascalCase: drop the underscores and capitalise each word, so
  `Undo_returns_the_state_pushed_before_the_edit` becomes `UndoReturnsTheStatePushedBeforeTheEdit`.
- FluentAssertions `because` arguments become `//` comments on the same line.

| FluentAssertions | xunit |
|---|---|
| `x.Should().Be(y)` | `Assert.Equal(y, x)` |
| `x.Should().NotBe(y)` | `Assert.NotEqual(y, x)` |
| `x.Should().BeTrue()` / `BeFalse()` | `Assert.True(x)` / `Assert.False(x)` |
| `x.Should().BeNull()` / `NotBeNull()` | `Assert.Null(x)` / `Assert.NotNull(x)` |
| `x.Should().BeApproximately(v, tol)` | `Assert.Equal(v, x, tol)` |
| `xs.Should().Equal(a, b)` / `.Equal(list)` | `Assert.Equal([a, b], xs)` / `Assert.Equal(list, xs)` |
| `xs.Should().BeEquivalentTo(ys)` | `Assert.Equivalent(ys, xs)` (`Assert.Equal` where the order is meant) |
| `xs.Should().ContainSingle()` | `Assert.Single(xs)` (keep the returned item if it was `.Which`) |
| `xs.Should().ContainSingle(p)` | `Assert.Single(xs, p)` |
| `xs.Should().HaveCount(n)` | `Assert.Equal(n, xs.Count)` (`.Count()` for an `IEnumerable`) |
| `xs.Should().Contain(x)` / `NotContain(x)` | `Assert.Contains(x, xs)` / `Assert.DoesNotContain(x, xs)` |
| `s.Should().Contain("t")` | `Assert.Contains("t", s, StringComparison.Ordinal)` |
| `xs.Should().BeEmpty()` / `NotBeEmpty()` | `Assert.Empty(xs)` / `Assert.NotEmpty(xs)` |
| `xs.Should().OnlyContain(p)` | `Assert.All(xs, i => Assert.True(p(i)))` |
| `x.Should().Match(p)` (object) | `Assert.True(p(x))` |
| `s.Should().Match("a*b")` (wildcard) | `Assert.Matches("^a.*b$", s)` |
| `x.Should().BeLessThan(y)` etc. | `Assert.True(x < y, $"{x} < {y}")` |
| `xs.Should().OnlyHaveUniqueItems()` | `Assert.Equal(xs.Count, xs.Distinct().Count())` |
| `xs.Should().BeInAscendingOrder(k)` | `Assert.Equal(xs.OrderBy(k), xs)` |
| `d.Should().ContainKeys(a, b)` | `Assert.Contains(a, d.Keys); Assert.Contains(b, d.Keys);` |
| `a.Should().NotIntersectWith(b)` | `Assert.Empty(a.Intersect(b))` |
| `s.Should().NotBeNullOrWhiteSpace()` | `Assert.False(string.IsNullOrWhiteSpace(s))` |
| `act.Should().Throw<T>()` | `Assert.Throws<T>(act)`; use `Assert.ThrowsAny<T>` if a derived type is thrown |
| `….WithMessage("*text*")` | `var e = Assert.Throws<T>(act); Assert.Contains("text", e.Message, StringComparison.Ordinal);` |
| `await act.Should().ThrowAsync<T>()` | `await Assert.ThrowsAsync<T>(act)` |
| `act.Should().NotThrow()` | call `act()` |

`PartitionAssertions.ShouldBeValidPartitionOf(this IReadOnlyList<Segment>, double)` becomes
`static class PartitionAssert { public static void Valid(IReadOnlyList<Segment> segments, double duration) }` with the
same checks, written with `Assert.*`. Update its callers.

- [ ] **Step 4: `SegmentEditor` (new): tests first**

`Tests/MainframeEngine.Video.Tests/SegmentEditorTests.cs`:

```csharp
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Tests;

public sealed class SegmentEditorTests
{
    private static SegmentEditor ThreeSegments()
    {
        // kept [0,4) · cut [4,6) · kept [6,10)
        var list = new SegmentList(10.0,
        [
            Segment.Create(0, 4, enabled: true, SegmentOrigin.Auto),
            Segment.Create(4, 6, enabled: false, SegmentOrigin.Auto, "silence 2.00s"),
            Segment.Create(6, 10, enabled: true, SegmentOrigin.Auto),
        ]);
        return new SegmentEditor(list, new EditHistory<Segment[]>(100));
    }

    [Fact]
    public void AnEditThatChangesNothingLeavesNoUndoStep()
    {
        var editor = ThreeSegments();
        Assert.False(editor.Edit(_ => { }));
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void AnEditIsOneUndoStepAndUndoRestoresThePartition()
    {
        var editor = ThreeSegments();
        var before = editor.Segments.Segments.ToArray();

        Assert.True(editor.Toggle(1));
        Assert.True(editor.Segments.Segments[1].Enabled);

        Assert.True(editor.Undo());
        Assert.Equal(before, editor.Segments.Segments);
        Assert.False(editor.History.CanUndo);
        Assert.True(editor.History.CanRedo);
    }

    [Fact]
    public void RedoReappliesAnUndoneEdit()
    {
        var editor = ThreeSegments();
        editor.Toggle(0);
        var after = editor.Segments.Segments.ToArray();
        editor.Undo();
        Assert.True(editor.Redo());
        Assert.Equal(after, editor.Segments.Segments);
    }

    [Fact]
    public void UndoAndRedoWithNothingToDoReturnFalse()
    {
        var editor = ThreeSegments();
        Assert.False(editor.Undo());
        Assert.False(editor.Redo());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void ToggleAndDissolveOutsideTheListDoNothing(int index)
    {
        var editor = ThreeSegments();
        Assert.False(editor.Toggle(index));
        Assert.False(editor.Dissolve(index));
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void DissolveIsOneUndoStep()
    {
        var editor = ThreeSegments();
        Assert.True(editor.Dissolve(1));
        Assert.Equal(1, editor.Segments.Count); // both neighbours are kept, so they merge over the removed cut
        editor.Undo();
        Assert.Equal(3, editor.Segments.Count);
    }

    [Fact]
    public void ASectionDrawnOverKeptFootageCutsIt()
    {
        var editor = ThreeSegments();
        Assert.True(editor.CreateSection(1, 2, anchor: 1));
        var cut = editor.Segments.Segments[editor.Segments.IndexAt(1.5)];
        Assert.False(cut.Enabled);
        Assert.Equal(SegmentOrigin.Manual, cut.Origin);
    }

    [Fact]
    public void ASectionDrawnInsideACutRestoresThatPart()
    {
        var editor = ThreeSegments();
        Assert.True(editor.CreateSection(4.5, 5.5, anchor: 4.5));
        Assert.True(editor.Segments.Segments[editor.Segments.IndexAt(5)].Enabled);
    }

    [Fact]
    public void ASectionAnchoredOffTheTimelineDoesNothing()
    {
        var editor = ThreeSegments();
        Assert.False(editor.CreateSection(1, 2, anchor: 11));
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void ABoundaryDragIsOneUndoStep()
    {
        var editor = ThreeSegments();
        var before = editor.Segments.Segments.ToArray();

        editor.BeginBoundaryDrag();
        Assert.True(editor.IsDragging);
        editor.UpdateBoundaryDrag(1, 3.5);
        editor.UpdateBoundaryDrag(1, 3.0);
        Assert.Equal(3.2, editor.EndBoundaryDrag(1, 3.2), 9);

        Assert.False(editor.IsDragging);
        editor.Undo();
        Assert.Equal(before, editor.Segments.Segments); // the whole drag is undone at once
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void APressAndReleaseOnABoundaryLeavesNoStep()
    {
        // A move and back would leave the cut marked manual (MoveBoundary marks disabled neighbours manual), which is a
        // real change; a press and release without moving is not.
        var editor = ThreeSegments();
        editor.BeginBoundaryDrag();
        editor.EndBoundaryDrag(1, 4.0);
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void UndoDuringADragDropsTheDragSnapshot()
    {
        var editor = ThreeSegments();
        editor.Toggle(0);
        editor.BeginBoundaryDrag();
        editor.Undo();
        Assert.False(editor.IsDragging);
        Assert.True(editor.History.CanRedo);
    }

    [Fact]
    public void ANewEditorIsClean() => Assert.False(ThreeSegments().IsDirty);

    [Fact]
    public void EditsMakeItDirtyAndMarkSavedMakesItClean()
    {
        var editor = ThreeSegments();
        var flips = 0;
        editor.DirtyChanged += (_, _) => flips++;
        editor.Toggle(1);
        Assert.True(editor.IsDirty);
        editor.MarkSaved();
        Assert.False(editor.IsDirty);
        Assert.Equal(2, flips);
    }

    [Fact]
    public void UndoingBackToTheSavedStateIsClean()
    {
        var editor = ThreeSegments();
        editor.Toggle(1);
        editor.Toggle(0);
        editor.Undo();
        Assert.True(editor.IsDirty);
        editor.Undo();
        Assert.False(editor.IsDirty); // the partition equals the saved one again
    }

    [Fact]
    public void DirtyChangedFiresOnlyWhenTheFlagFlips()
    {
        var editor = ThreeSegments();
        var flips = 0;
        editor.DirtyChanged += (_, _) => flips++;
        editor.Toggle(1);
        editor.Toggle(0);
        editor.Toggle(2);
        Assert.Equal(1, flips);
    }
}
```

Run: `dotnet test Tests/MainframeEngine.Video.Tests --filter FullyQualifiedName~SegmentEditorTests`
Expected: the build fails, because `SegmentEditor` does not exist.

- [ ] **Step 5: `SegmentEditor`**

`MainframeEngine.Video/SegmentEditor.cs`, in engine style (braces only where the statement spans lines):

```csharp
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video;

/// <summary>
/// One editing surface over a <see cref="SegmentList"/>, so the timeline, the transcript and every detector follow the
/// same rules:
/// <list type="bullet">
/// <item>Each edit is one undo step, and an edit that changes nothing leaves none.</item>
/// <item>A boundary drag is one step, from Begin to End.</item>
/// <item>A drawn section takes the opposite state of the segment it was anchored in.</item>
/// <item><see cref="IsDirty"/> compares with the last save, so undoing back to the saved partition is clean.</item>
/// </list>
/// Undo snapshots are arrays of immutable <see cref="Segment"/> records, so a deep history stays cheap.
/// </summary>
public sealed class SegmentEditor
{
    private Segment[] _saved;
    private Segment[]? _dragSnapshot;
    private bool _dirty;

    /// <summary>
    /// Edits <paramref name="segments"/>, with <paramref name="history"/> as its undo stack. The current partition counts
    /// as saved.
    /// </summary>
    public SegmentEditor(SegmentList segments, EditHistory<Segment[]> history)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(history);
        Segments = segments;
        History = history;
        _saved = [.. segments.Segments];
        segments.Changed += (_, _) => UpdateDirty();
    }

    public SegmentList Segments { get; }

    /// <summary>The undo stack; its <c>Changed</c> event drives Undo/Redo availability.</summary>
    public EditHistory<Segment[]> History { get; }

    /// <summary>True while the partition differs from the one last marked saved.</summary>
    public bool IsDirty => _dirty;

    /// <summary>True between <see cref="BeginBoundaryDrag"/> and <see cref="EndBoundaryDrag"/>.</summary>
    public bool IsDragging => _dragSnapshot is not null;

    /// <summary>Raised when <see cref="IsDirty"/> flips.</summary>
    public event EventHandler? DirtyChanged;

    /// <summary>Runs <paramref name="edit"/> as one undo step; false (and no step) when it changed nothing.</summary>
    public bool Edit(Action<SegmentList> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var before = Segments.Segments.ToArray();
        edit(Segments);
        if (before.SequenceEqual(Segments.Segments))
            return false;
        History.Push(before);
        return true;
    }

    /// <summary>Flips the segment at <paramref name="index"/>, which becomes manual; false when out of range.</summary>
    public bool Toggle(int index) => index >= 0 && index < Segments.Count && Edit(s => s.Toggle(index));

    /// <summary>Removes the region at <paramref name="index"/> and lets its neighbours grow over it; false when out of range.</summary>
    public bool Dissolve(int index) => index >= 0 && index < Segments.Count && Edit(s => s.Dissolve(index));

    /// <summary>
    /// Makes [<paramref name="start"/>, <paramref name="end"/>) one manual section with the opposite state of the segment at
    /// <paramref name="anchor"/>, where the drag started. Drawing over kept footage cuts it; drawing inside a cut restores
    /// that part. False when the anchor is off the timeline or nothing changed.
    /// </summary>
    public bool CreateSection(double start, double end, double anchor)
    {
        var anchorIndex = Segments.IndexAt(anchor);
        if (anchorIndex < 0)
            return false;
        var enabled = !Segments.Segments[anchorIndex].Enabled;
        return Edit(s => s.SetRange(start, end, enabled));
    }

    /// <summary>Starts a boundary drag: the partition as it is now is what one undo restores.</summary>
    public void BeginBoundaryDrag() => _dragSnapshot = [.. Segments.Segments];

    /// <summary>Moves the boundary live during a drag; returns the time applied after clamping.</summary>
    public double UpdateBoundaryDrag(int boundaryIndex, double time) => Segments.MoveBoundary(boundaryIndex, time);

    /// <summary>
    /// Ends a boundary drag at <paramref name="time"/>, which the caller has already snapped. Records the whole drag as one
    /// undo step, or none when the partition is back where it started.
    /// </summary>
    public double EndBoundaryDrag(int boundaryIndex, double time)
    {
        var applied = Segments.MoveBoundary(boundaryIndex, time);
        if (_dragSnapshot is { } before && !before.SequenceEqual(Segments.Segments))
            History.Push(before);
        _dragSnapshot = null;
        return applied;
    }

    /// <summary>Restores the partition from before the last edit; false when there is none.</summary>
    public bool Undo()
    {
        if (!History.CanUndo)
            return false;
        _dragSnapshot = null;
        Segments.Restore(History.Undo(Segments.Segments.ToArray()));
        return true;
    }

    /// <summary>Re-applies the last undone edit; false when there is none.</summary>
    public bool Redo()
    {
        if (!History.CanRedo)
            return false;
        _dragSnapshot = null;
        Segments.Restore(History.Redo(Segments.Segments.ToArray()));
        return true;
    }

    /// <summary>Marks the current partition as what is on disk now.</summary>
    public void MarkSaved()
    {
        _saved = [.. Segments.Segments];
        UpdateDirty();
    }

    private void UpdateDirty()
    {
        var dirty = !_saved.SequenceEqual(Segments.Segments);
        if (dirty == _dirty)
            return;
        _dirty = dirty;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }
}
```

- [ ] **Step 6: Build, fix analyzer findings, format**

Run: `dotnet build MainframeEngine.Video && dotnet build Tests/MainframeEngine.Video.Tests`
Fix every analyzer finding at `latest-recommended` (for example culture-sensitive formatting, `CA1305`) by changing the
code, not by suppressing it. The one exception is a naming or style finding that the copied tests depend on: put that
in a `NoWarn` in the test csproj, with a comment, the way `Tests/Directory.Build.props` already does for CA1515.

Then run `dotnet format MainframeEngine.Video` and `dotnet format Tests/MainframeEngine.Video.Tests`, so the copied
code matches the engine's `.editorconfig`.

- [ ] **Step 7: Run the tests**

Run: `dotnet test Tests/MainframeEngine.Video.Tests`
Expected: PASS, with **282 + 3 + 17 = 302** tests: Cutback's Core tests, the new extension theory and the
`SegmentEditor` tests.

- [ ] **Step 8: Gates and commit**

```bash
just build && just format-check
git add MainframeEngine.Video Tests/MainframeEngine.Video.Tests MainframeEngine.slnx justfile .github/workflows/ci.yml build/linux/inside.sh
git commit -m "Add MainframeEngine.Video with Cutback's Core logic and a shared edit session

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Media ported: ffprobe JSON instead of FFMpegCore, `PreviewDecoding`, `DeliveryEncoder`

**Files:**
- Create: `MainframeEngine.Video/Media/**`, every file of `src/Cutback.Media/`. The root files go in `Media/`, and
  `Export/` becomes `Media/Export/`.
- Create: `MainframeEngine.Video/Media/FfprobeJson.cs`, `MainframeEngine.Video/Media/Preview/PreviewDecoding.cs` and
  `MainframeEngine.Video/Media/Export/DeliveryEncoder.cs`.
- Modify:
  - `MainframeEngine.Video/Media/MediaProbe.cs`: no FFMpegCore.
  - `Media/FfmpegProcess.cs`: public.
  - `Media/TempSession.cs`: the folder name.
  - `Media/Export/Exporter.cs` and `ExportRequest.cs`: the encoder choice.
  - `Media/FfmpegNotFoundException.cs`: renamed to `FfmpegMissingException.cs`.
- Create: `Tests/MainframeEngine.Video.Tests/Media/**`, every file of `tests/Cutback.Media.Tests/` converted with Task
  1's table, plus `FfprobeJsonTests.cs`, `PreviewDecodingTests.cs` and `DeliveryEncoderTests.cs`.

**Interfaces:**
- Consumes: Task 1's models.
- Produces:
  - `MainframeEngine.Video.Media`: `FfmpegLocator`, `FfmpegLocation`, `FfmpegLocationSource`, `FfmpegMissingException`,
    `FfmpegException`, `FfmpegProcess`, `FfmpegCapabilities`, `FfmpegProgressParser`, `MediaProbe`, `MediaInfo`,
    `MediaProbeException`, `Waveform`, `WaveformLevel`, `WaveformExtractor`, `PcmExtractor`, `Peak`, `PeakReducer`,
    `SilenceDetector`, `SilenceDetectParser`, `TempSession`.
  - `….Media.Export`: `Exporter`, `ExportRequest`, `ExportProgress`, `ExportMode`, `FilterGraphBuilder`,
    `KeyframeSnapper`, `DeliveryEncoder`.
  - `….Media.Preview`: `PreviewDecoding`.

  These are new or changed:
  ```csharp
  public static class FfmpegProcess { /* now public: Start, StartRaw, WaitForSuccessAsync, TryKill */ }
  internal static class FfprobeJson { public static MediaInfo Parse(string json, string path); }   // throws MediaProbeException
  public sealed record ExportRequest(string SourcePath, IReadOnlyList<Segment> Segments, double DurationSeconds,
      string OutputPath, ExportMode Mode, int Width = 0, int Height = 0, double FrameRate = 0);
  public static class PreviewDecoding
  {
      public const int MaxWidth = 960;
      public const double MaxFrameRate = 30;
      public static (int Width, int Height) PreviewSize(int sourceWidth, int sourceHeight, int maxWidth = MaxWidth);
      public static double PreviewFrameRate(double sourceFrameRate);
      public static IReadOnlyList<string> VideoArguments(string source, double start, double end, int width, int height, double frameRate);
      public static IReadOnlyList<string> AudioArguments(string source, double start, double end, int sampleRate);
      public static IReadOnlyList<string> FrameArguments(string source, double time, int width, int height);
  }
  public static class DeliveryEncoder
  {
      public const string Software = "libx264";
      public const string Fallback = "libopenh264";
      public static IReadOnlyList<string> ParseEncoders(string output);
      public static IReadOnlyList<string> Candidates(OSPlatform os, IReadOnlyCollection<string> available);
      public static long TargetBitrate(int width, int height, double frameRate);
      public static IReadOnlyList<string> VideoArguments(string encoder, long bitrate);
      public static IReadOnlyList<string> ProbeArguments(string encoder);
      public static string NoEncoderMessage(OSPlatform os);
      public static Task<string> SelectAsync(FfmpegLocation ffmpeg, CancellationToken cancellationToken);
  }
  ```

- [ ] **Step 1: Copy, rewrite the namespaces, rename**

```bash
mkdir -p MainframeEngine.Video/Media Tests/MainframeEngine.Video.Tests/Media
cp -R /tmp/cutback-src/src/Cutback.Media/. MainframeEngine.Video/Media/
rm -f MainframeEngine.Video/Media/Cutback.Media.csproj MainframeEngine.Video/Media/LICENSE MainframeEngine.Video/Media/README.md
cp /tmp/cutback-src/tests/Cutback.Media.Tests/*.cs Tests/MainframeEngine.Video.Tests/Media/
files=$(grep -rl "Cutback\.\(Media\|Core\)" MainframeEngine.Video Tests/MainframeEngine.Video.Tests --include='*.cs')
sed -i '' -e 's/\bCutback\.Media\b/MainframeEngine.Video.Media/g' -e 's/\bCutback\.Core\b/MainframeEngine.Video/g' $files
grep -rl "FfmpegNotFoundException" MainframeEngine.Video Tests/MainframeEngine.Video.Tests --include='*.cs' | xargs sed -i '' -e 's/\bFfmpegNotFoundException\b/FfmpegMissingException/g'
mv MainframeEngine.Video/Media/FfmpegNotFoundException.cs MainframeEngine.Video/Media/FfmpegMissingException.cs
```

Then:
- `FfmpegProcess` becomes `public static class`. Its new summary says it is public so the editor's preview decodes
  with the same preamble and error reporting. Callers must read stderr (`StandardError.ReadToEndAsync`) or ffmpeg
  blocks, and binary output comes from `StandardOutput.BaseStream`.
- `TempSession`'s folder becomes `Path.Combine(Path.GetTempPath(), "MainframeVideo", Guid.NewGuid().ToString("N"))`.
- User-facing messages that say "Cutback" now say "the video editor".

- [ ] **Step 2: Replace FFMpegCore with `ffprobe -of json`: tests first**

`Tests/MainframeEngine.Video.Tests/Media/FfprobeJsonTests.cs`:

```csharp
using MainframeEngine.Video.Media;

namespace MainframeEngine.Video.Tests.Media;

public sealed class FfprobeJsonTests
{
    private const string Landscape = """
        { "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "r_frame_rate": "60/1", "avg_frame_rate": "60/1" },
            { "codec_type": "audio", "codec_name": "aac" } ],
          "format": { "duration": "61.250000" } }
        """;

    [Fact]
    public void ReadsDurationSizeRateAndStreams()
    {
        var info = FfprobeJson.Parse(Landscape, "/r/a.mp4");
        Assert.Equal(new MediaInfo(61.25, 1920, 1080, 60, HasAudio: true, VideoCodec: "h264", AudioCodec: "aac"), info);
    }

    [Fact]
    public void ARotatedPhoneRecordingReportsItsDisplayedSize()
    {
        const string json = """
            { "streams": [ { "codec_type": "video", "codec_name": "hevc", "width": 1920, "height": 1080, "avg_frame_rate": "30000/1001",
                             "side_data_list": [ { "rotation": -90 } ] } ],
              "format": { "duration": "5" } }
            """;
        var info = FfprobeJson.Parse(json, "/r/p.mov");
        Assert.Equal((1080, 1920), (info.Width, info.Height));
        Assert.Equal(29.97, info.FrameRate, 2);
        Assert.False(info.HasAudio);
    }

    [Fact]
    public void ABogusRealFrameRateFallsBackToTheAverage()
    {
        const string json = """
            { "streams": [ { "codec_type": "video", "width": 640, "height": 360, "r_frame_rate": "90000/1", "avg_frame_rate": "24/1" } ],
              "format": { "duration": "2" } }
            """;
        Assert.Equal(24, FfprobeJson.Parse(json, "/r/v.mkv").FrameRate);
    }

    [Theory]
    [InlineData("""{ "streams": [ { "codec_type": "audio" } ], "format": { "duration": "3" } }""", "no video stream")]
    [InlineData("""{ "streams": [ { "codec_type": "video", "width": 2, "height": 2 } ], "format": { } }""", "no duration")]
    [InlineData("not json", "could not read")]
    public void UnusableOutputIsAMediaProbeException(string json, string message)
    {
        var e = Assert.Throws<MediaProbeException>(() => FfprobeJson.Parse(json, "/r/x.mp4"));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }
}
```

`MainframeEngine.Video/Media/FfprobeJson.cs` reads the output with `JsonDocument`:
- **Video stream:** the first stream with `codec_type == "video"`. It is required.
- **Duration:** `format.duration`, parsed with the invariant culture. It must be finite and greater than 0.
- **Size:** `width`/`height`, swapped when `|rotation| % 180 == 90`. The rotation comes from a `side_data_list` entry's
  `rotation`, or from `tags.rotate`.
- **Frame rate:** the `avg_frame_rate` fraction when it is between 0 and 1000 (exclusive), else `r_frame_rate`.
- **Audio:** whether any stream has `codec_type == "audio"`, plus both codec names.
- **Messages,** in Cutback's wording:
  - `"\"{name}\" has no video stream."`;
  - `"\"{name}\" reports no duration. Live streams and some raw formats are not supported."`;
  - for invalid JSON, `"ffprobe could not read \"{name}\"…"`.

`MediaProbe.ProbeAsync` then:
1. Runs `FfmpegProcess.StartRaw(_ffmpeg.FfprobePath, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path])`.
2. Reads stdout to the end.
3. Calls `WaitForSuccessAsync`, turning an `FfmpegException` into a `MediaProbeException` with Cutback's "may not be a
   video file, or it may be damaged" message.
4. Returns `FfprobeJson.Parse(stdout, path)`.

Delete `FromAnalysis` and every `using FFMpegCore`.

- [ ] **Step 3: `PreviewDecoding`: tests first**

`Tests/MainframeEngine.Video.Tests/Media/PreviewDecodingTests.cs`:

```csharp
using MainframeEngine.Video.Media.Preview;

namespace MainframeEngine.Video.Tests.Media;

public sealed class PreviewDecodingTests
{
    [Theory]
    [InlineData(1920, 1080, 960, 540)]
    [InlineData(3840, 2160, 960, 540)]
    [InlineData(1280, 720, 960, 540)]
    [InlineData(640, 360, 640, 360)]
    [InlineData(721, 405, 720, 404)]
    [InlineData(1000, 1000, 960, 960)]
    public void PreviewSizeCapsTheWidthKeepsTheAspectAndStaysEven(int w, int h, int ew, int eh) =>
        Assert.Equal((ew, eh), PreviewDecoding.PreviewSize(w, h));

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-4, 4)]
    public void PreviewSizeRejectsADegenerateSource(int w, int h) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PreviewDecoding.PreviewSize(w, h));

    [Theory]
    [InlineData(60.0, 30.0)]
    [InlineData(30.0, 30.0)]
    [InlineData(29.97, 29.97)]
    [InlineData(24.0, 24.0)]
    [InlineData(0.0, 30.0)]
    [InlineData(double.NaN, 30.0)]
    public void PreviewFrameRateIsTheSourceRateCappedAt30(double source, double expected) =>
        Assert.Equal(expected, PreviewDecoding.PreviewFrameRate(source));

    [Fact]
    public void VideoArgumentsSeekScaleAndEmitRawRgba() =>
        Assert.Equal(
            ["-ss", "4.86", "-i", "/r/a.mp4", "-t", "7.18", "-an", "-vf", "fps=30,scale=960:540:flags=bilinear",
             "-pix_fmt", "rgba", "-f", "rawvideo", "-"],
            PreviewDecoding.VideoArguments("/r/a.mp4", 4.86, 12.04, 960, 540, 30));

    [Fact]
    public void AudioArgumentsFadeEachEndAndEmitStereoFloat() =>
        Assert.Equal(
            ["-ss", "4.86", "-i", "/r/a.mp4", "-t", "7.18", "-vn", "-af", "afade=t=in:st=0:d=0.008,afade=t=out:st=7.172:d=0.008",
             "-ac", "2", "-ar", "48000", "-f", "f32le", "-"],
            PreviewDecoding.AudioArguments("/r/a.mp4", 4.86, 12.04, 48000));

    [Fact]
    public void TheAudioFadeShrinksForVeryShortSegments() =>
        Assert.Contains("afade=t=in:st=0:d=0.005,afade=t=out:st=0.005:d=0.005", PreviewDecoding.AudioArguments("/r/a.mp4", 1.0, 1.01, 48000));

    [Fact]
    public void FrameArgumentsDecodeOneScaledFrame() =>
        Assert.Equal(
            ["-ss", "3.5", "-i", "/r/a.mp4", "-frames:v", "1", "-an", "-vf", "scale=960:540:flags=bilinear",
             "-pix_fmt", "rgba", "-f", "rawvideo", "-"],
            PreviewDecoding.FrameArguments("/r/a.mp4", 3.5, 960, 540));

    [Fact]
    public void AnEmptyRangeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PreviewDecoding.VideoArguments("/r/a.mp4", 5, 5, 960, 540, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => PreviewDecoding.AudioArguments("/r/a.mp4", 6, 5, 48000));
    }
}
```

`MainframeEngine.Video/Media/Preview/PreviewDecoding.cs`:

```csharp
using System.Globalization;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Video.Media.Preview;

/// <summary>
/// ffmpeg arguments for the editor's gapless preview. Each kept range gets one video decode (raw RGBA frames on stdout)
/// and one audio decode (32-bit float stereo on stdout); scrubbing decodes a single frame. Every filter is a fixed size,
/// so passing it inline cannot grow with the cut count. That is unlike the export graph, which goes in a file. Audio gets
/// the export's 8 ms fades, so the preview sounds like the result. <see cref="FfmpegProcess.Start"/> adds the preamble.
/// </summary>
public static class PreviewDecoding
{
    /// <summary>Preview frames are at most this wide: enough for the preview pane, at a quarter of the upload cost of 1080p.</summary>
    public const int MaxWidth = 960;

    /// <summary>Preview frames are decoded at no more than this rate.</summary>
    public const double MaxFrameRate = 30;

    /// <summary>The preview size: at most <paramref name="maxWidth"/> wide, the source's aspect, both sides even.</summary>
    public static (int Width, int Height) PreviewSize(int sourceWidth, int sourceHeight, int maxWidth = MaxWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        var width = Even(Math.Min(sourceWidth, maxWidth));
        return (width, Even(sourceHeight * (double)width / sourceWidth));
    }

    /// <summary>The source rate capped at <see cref="MaxFrameRate"/>. An unknown rate previews at the cap.</summary>
    public static double PreviewFrameRate(double sourceFrameRate) =>
        double.IsFinite(sourceFrameRate) && sourceFrameRate > 0 ? Math.Min(sourceFrameRate, MaxFrameRate) : MaxFrameRate;

    /// <summary>Raw RGBA frames of [<paramref name="start"/>, <paramref name="end"/>) at the preview size and rate.</summary>
    public static IReadOnlyList<string> VideoArguments(string source, double start, double end, int width, int height, double frameRate)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        var length = Length(start, end);
        return
        [
            "-ss", FilterGraphBuilder.F(start), "-i", source, "-t", FilterGraphBuilder.F(length), "-an",
            "-vf", $"fps={FilterGraphBuilder.F(frameRate)},scale={width}:{height}:flags=bilinear",
            "-pix_fmt", "rgba", "-f", "rawvideo", "-",
        ];
    }

    /// <summary>Interleaved stereo float samples of [<paramref name="start"/>, <paramref name="end"/>), faded at each end.</summary>
    public static IReadOnlyList<string> AudioArguments(string source, double start, double end, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var length = Length(start, end);
        var fade = Math.Min(FilterGraphBuilder.FadeSeconds, length / 2);
        var f = FilterGraphBuilder.F(fade);
        return
        [
            "-ss", FilterGraphBuilder.F(start), "-i", source, "-t", FilterGraphBuilder.F(length), "-vn",
            "-af", $"afade=t=in:st=0:d={f},afade=t=out:st={FilterGraphBuilder.F(length - fade)}:d={f}",
            "-ac", "2", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-",
        ];
    }

    /// <summary>One raw RGBA frame at <paramref name="time"/>, for scrubbing while paused.</summary>
    public static IReadOnlyList<string> FrameArguments(string source, double time, int width, int height)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        return
        [
            "-ss", FilterGraphBuilder.F(Math.Max(0, time)), "-i", source, "-frames:v", "1", "-an",
            "-vf", $"scale={width}:{height}:flags=bilinear", "-pix_fmt", "rgba", "-f", "rawvideo", "-",
        ];
    }

    private static double Length(double start, double end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (!(end > start))
            throw new ArgumentOutOfRangeException(nameof(end), end, "The range must end after it starts.");
        return end - start;
    }

    private static int Even(double value) => Math.Max(2, 2 * (int)Math.Round(value / 2, MidpointRounding.AwayFromZero));
}
```

- [ ] **Step 4: `DeliveryEncoder` and the export fallback: tests first**

`Tests/MainframeEngine.Video.Tests/Media/DeliveryEncoderTests.cs`:

```csharp
using System.Runtime.InteropServices;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Video.Tests.Media;

public sealed class DeliveryEncoderTests
{
    private const string Listing = """
        Encoders:
         V..... = Video
         A..... = Audio
         ------
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D libopenh264          OpenH264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V....D h264_videotoolbox    VideoToolbox H.264 Encoder (codec h264)
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         A....D aac                  AAC (Advanced Audio Coding)
        """;

    [Fact]
    public void ParseEncodersListsNamesAndSkipsTheLegend() =>
        Assert.Equal(["libx264", "libopenh264", "h264_videotoolbox", "h264_nvenc", "aac"], DeliveryEncoder.ParseEncoders(Listing));

    [Fact]
    public void CandidatesPutLibx264FirstThenHardwareThenOpenH264()
    {
        var available = DeliveryEncoder.ParseEncoders(Listing);
        Assert.Equal(["libx264", "h264_videotoolbox", "libopenh264"], DeliveryEncoder.Candidates(OSPlatform.OSX, available));
        Assert.Equal(["libx264", "h264_nvenc", "libopenh264"], DeliveryEncoder.Candidates(OSPlatform.Linux, available));
    }

    [Fact]
    public void CandidatesOnWindowsTryEveryVendorInOrder() =>
        Assert.Equal(["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libopenh264"],
            DeliveryEncoder.Candidates(OSPlatform.Windows, ["h264_mf", "h264_amf", "h264_qsv", "h264_nvenc", "libopenh264"]));

    [Fact]
    public void CandidatesAreEmptyWithoutAnH264Encoder() =>
        Assert.Empty(DeliveryEncoder.Candidates(OSPlatform.OSX, ["aac", "libvpx-vp9"]));

    [Fact]
    public void Libx264KeepsConstantQuality() =>
        Assert.Equal(["-c:v", "libx264", "-crf", "20", "-preset", "medium", "-pix_fmt", "yuv420p"], DeliveryEncoder.VideoArguments("libx264", 12_000_000));

    [Fact]
    public void OtherEncodersGetABitrateTarget() =>
        Assert.Equal(["-c:v", "h264_videotoolbox", "-b:v", "12000000", "-maxrate", "18000000", "-bufsize", "24000000", "-pix_fmt", "yuv420p"],
            DeliveryEncoder.VideoArguments("h264_videotoolbox", 12_000_000));

    [Theory]
    [InlineData(1920, 1080, 60.0, 12_441_600)]
    [InlineData(1280, 720, 30.0, 2_764_800)]
    [InlineData(3840, 2160, 60.0, 49_766_400)]
    [InlineData(320, 180, 30.0, 2_000_000)]
    [InlineData(0, 0, 0.0, 12_000_000)]
    public void TargetBitrateIsATenthOfABitPerPixelClamped(int w, int h, double fps, long expected) =>
        Assert.Equal(expected, DeliveryEncoder.TargetBitrate(w, h, fps));

    [Fact]
    public void ProbeArgumentsEncodeOneTinyFrameToNowhere() =>
        Assert.Equal(["-f", "lavfi", "-i", "color=s=256x256:d=0.1", "-c:v", "h264_nvenc", "-f", "null", "-"], DeliveryEncoder.ProbeArguments("h264_nvenc"));

    [Fact]
    public void TheNoEncoderMessageNamesLibx264AndTheInstallCommand()
    {
        Assert.Contains("brew install ffmpeg", DeliveryEncoder.NoEncoderMessage(OSPlatform.OSX), StringComparison.Ordinal);
        Assert.Contains("libx264", DeliveryEncoder.NoEncoderMessage(OSPlatform.OSX), StringComparison.Ordinal);
        Assert.Contains("winget install", DeliveryEncoder.NoEncoderMessage(OSPlatform.Windows), StringComparison.Ordinal);
        Assert.Contains("package manager", DeliveryEncoder.NoEncoderMessage(OSPlatform.Linux), StringComparison.Ordinal);
    }
}
```

`MainframeEngine.Video/Media/Export/DeliveryEncoder.cs`:

```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MainframeEngine.Video.Media.Export;

/// <summary>
/// Chooses the H.264 encoder for a precise export, best first: libx264 (constant quality), then the platform's hardware
/// encoder, then libopenh264 (found in LGPL ffmpeg builds). Export therefore still works on an ffmpeg built without x264.
/// ffmpeg lists hardware encoders even on machines that lack the hardware, so each candidate is probe-encoded before use,
/// as the recorder's <c>EncoderSelector</c> does. The choice is cached per ffmpeg binary.
/// </summary>
public static class DeliveryEncoder
{
    public const string Software = "libx264";
    public const string Fallback = "libopenh264";

    private const double BitsPerPixel = 0.1;
    private const long MinBitrate = 2_000_000;
    private const long MaxBitrate = 80_000_000;
    private const long UnknownBitrate = 12_000_000;

    private static readonly ConcurrentDictionary<string, Task<string>> Selected = new(StringComparer.Ordinal);

    /// <summary>Encoder names from <c>ffmpeg -encoders</c> (lines like <c> V....D libx264 …</c>), skipping the legend.</summary>
    public static IReadOnlyList<string> ParseEncoders(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return
        [
            .. output.Split('\n')
                .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] is 'V' or 'A' or 'S' && parts[1] != "=")
                .Select(parts => parts[1]),
        ];
    }

    /// <summary>The encoders to try, best first, out of those this ffmpeg was built with.</summary>
    public static IReadOnlyList<string> Candidates(OSPlatform os, IReadOnlyCollection<string> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        string[] hardware = os == OSPlatform.OSX ? ["h264_videotoolbox"]
            : os == OSPlatform.Windows ? ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf"]
            : ["h264_nvenc"];
        return [.. new[] { Software }.Concat(hardware).Append(Fallback).Where(available.Contains)];
    }

    /// <summary>A bitrate for <paramref name="width"/>×<paramref name="height"/> at <paramref name="frameRate"/>; 12 Mbit/s when unknown.</summary>
    public static long TargetBitrate(int width, int height, double frameRate)
    {
        if (width <= 0 || height <= 0 || !(frameRate > 0))
            return UnknownBitrate;
        return Math.Clamp((long)Math.Round(width * (double)height * frameRate * BitsPerPixel), MinBitrate, MaxBitrate);
    }

    /// <summary>The video half of the codec arguments for <paramref name="encoder"/>.</summary>
    public static IReadOnlyList<string> VideoArguments(string encoder, long bitrate)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoder);
        if (encoder == Software)
            return ["-c:v", Software, "-crf", "20", "-preset", "medium", "-pix_fmt", "yuv420p"];
        return
        [
            "-c:v", encoder,
            "-b:v", bitrate.ToString(CultureInfo.InvariantCulture),
            "-maxrate", (bitrate * 3 / 2).ToString(CultureInfo.InvariantCulture),
            "-bufsize", (bitrate * 2).ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
        ];
    }

    /// <summary>Encodes one 256×256 frame to nowhere, which fails fast when the encoder's hardware or library is missing.</summary>
    public static IReadOnlyList<string> ProbeArguments(string encoder) =>
        ["-f", "lavfi", "-i", "color=s=256x256:d=0.1", "-c:v", encoder, "-f", "null", "-"];

    /// <summary>What to tell the user when no H.264 encoder works.</summary>
    public static string NoEncoderMessage(OSPlatform os)
    {
        var install = os == OSPlatform.OSX ? "Install one with \"brew install ffmpeg\"."
            : os == OSPlatform.Windows ? "Install one with \"winget install Gyan.FFmpeg\"."
            : "Install the ffmpeg package from your distribution's package manager.";
        return $"This ffmpeg has no working H.264 encoder (it was built without {Software}, and no hardware encoder or {Fallback} works). {install}";
    }

    /// <summary>The best working encoder for <paramref name="ffmpeg"/>, cached per binary.</summary>
    /// <exception cref="FfmpegException">None works (the message is <see cref="NoEncoderMessage"/>).</exception>
    public static Task<string> SelectAsync(FfmpegLocation ffmpeg, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        var task = Selected.GetOrAdd(ffmpeg.FfmpegPath, _ => SelectUncachedAsync(ffmpeg, cancellationToken));
        if (task.IsFaulted || task.IsCanceled)
            Selected.TryRemove(new KeyValuePair<string, Task<string>>(ffmpeg.FfmpegPath, task));
        return task;
    }

    private static async Task<string> SelectUncachedAsync(FfmpegLocation ffmpeg, CancellationToken ct)
    {
        var listing = await RunAsync(ffmpeg, ["-encoders"], ct).ConfigureAwait(false);
        var os = OperatingSystem.IsMacOS() ? OSPlatform.OSX : OperatingSystem.IsWindows() ? OSPlatform.Windows : OSPlatform.Linux;
        foreach (var candidate in Candidates(os, ParseEncoders(listing.Output)))
        {
            var probe = await RunAsync(ffmpeg, ProbeArguments(candidate), ct).ConfigureAwait(false);
            if (probe.ExitCode == 0)
                return candidate;
        }

        throw new FfmpegException(NoEncoderMessage(os), -1, string.Empty);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(FfmpegLocation ffmpeg, IEnumerable<string> args, CancellationToken ct)
    {
        using var process = FfmpegProcess.Start(ffmpeg.FfmpegPath, args, redirectStandardOutput: true);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            FfmpegProcess.TryKill(process);
            throw;
        }

        await stderr.ConfigureAwait(false);
        return (process.ExitCode, await stdout.ConfigureAwait(false));
    }
}
```

`ExportRequest`: add `int Width = 0, int Height = 0, double FrameRate = 0`, each with a `<param>` doc ("for the bitrate
of encoders without constant quality; 0 means unknown").

`Exporter.ExportPreciseAsync`: after `capabilities`, choose the encoder and bitrate:

```csharp
        var encoder = extension == ".webm" ? null : await DeliveryEncoder.SelectAsync(_ffmpeg, ct).ConfigureAwait(false);
        var bitrate = DeliveryEncoder.TargetBitrate(request.Width, request.Height, request.FrameRate);
```

Pass both into `CodecArguments`, which becomes:

```csharp
    private static IEnumerable<string> CodecArguments(string extension, string? encoder, long bitrate)
    {
        if (extension == ".webm")
            return ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k"];
        IEnumerable<string> video = DeliveryEncoder.VideoArguments(encoder ?? DeliveryEncoder.Software, bitrate);
        IEnumerable<string> audio = ["-c:a", "aac", "-b:a", "192k"];
        return extension is ".mp4" or ".mov" ? [.. video, .. audio, "-movflags", "+faststart"] : [.. video, .. audio];
    }
```

Update `ExportMode`'s doc for Precise so it names the fallback.

- [ ] **Step 5: Convert the copied Media tests**, using Task 1 Step 3's table and naming, into
  `namespace MainframeEngine.Video.Tests.Media`.

- [ ] **Step 6: Run, format, gates, commit**

Run: `dotnet format MainframeEngine.Video && dotnet format Tests/MainframeEngine.Video.Tests && dotnet test Tests/MainframeEngine.Video.Tests`
Expected: PASS. The total is **424**: 302 from Task 1, 83 Media, 6 FfprobeJson, 20 PreviewDecoding and 13 DeliveryEncoder.

Run `grep -rn "FFMpegCore" MainframeEngine.Video`. Expected: no hits.

```bash
just build && just format-check
git add MainframeEngine.Video Tests/MainframeEngine.Video.Tests
git commit -m "Port Cutback's media layer: ffprobe JSON probing, preview decoding, export without libx264

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Analysis ported: Whisper transcription and the model store

**Files:**
- Create: `MainframeEngine.Video/Analysis/**`, every file of `src/Cutback.Analysis/` except the csproj.
- Create: `Tests/MainframeEngine.Video.Tests/Analysis/**`, the three Analysis test files, converted.
- Modify:
  - `MainframeEngine.Video/MainframeEngine.Video.csproj`: the Whisper packages.
  - `Directory.Packages.props`: an `<!-- Editor -->` group.
  - `MainframeEngine.Video/Analysis/ModelStore.cs`: `CreateDefault` removed.
  - `THIRD_PARTY_NOTICES.md`: rows and sections.

**Interfaces:**
- Produces:
  - `MainframeEngine.Video.Analysis`: `ITranscriber`, `WhisperTranscriber`, `WordAssembler`, `TokenTiming`,
    `ModelStore`, `WhisperModel`, `WhisperModelInfo`, `ModelDownloadException`, `ModelLoadException`.
  - `ModelStore(string cacheDirectory)`, with `PathFor`, `IsDownloaded`, `EnsureAsync(model, progress, ct)` and `Delete`.
  - `WhisperTranscriber(FfmpegLocation, string modelPath, WhisperModel)`, with `TranscribeAsync(mediaPath, progress, ct)`.
  - `WhisperModelInfo`: `All`, `For`, `Parse(key)` and `Default` (base.en).
  - Not copied, as a spec non-goal: `ICutSuggester`, `NotImplementedCutSuggester` and `NotImplementedTranscriber`.
    Delete their tests if any exist.

- [ ] **Step 1: Copy, rewrite the namespaces, add the packages**

```bash
mkdir -p MainframeEngine.Video/Analysis Tests/MainframeEngine.Video.Tests/Analysis
cp /tmp/cutback-src/src/Cutback.Analysis/*.cs MainframeEngine.Video/Analysis/
rm MainframeEngine.Video/Analysis/ICutSuggester.cs MainframeEngine.Video/Analysis/NotImplementedCutSuggester.cs MainframeEngine.Video/Analysis/NotImplementedTranscriber.cs
cp /tmp/cutback-src/tests/Cutback.Analysis.Tests/*.cs Tests/MainframeEngine.Video.Tests/Analysis/
files=$(grep -rl "Cutback\." MainframeEngine.Video/Analysis Tests/MainframeEngine.Video.Tests/Analysis --include='*.cs')
sed -i '' -e 's/\bCutback\.Analysis\b/MainframeEngine.Video.Analysis/g' -e 's/\bCutback\.Media\b/MainframeEngine.Video.Media/g' -e 's/\bCutback\.Core\b/MainframeEngine.Video/g' $files
```

In `Directory.Packages.props`, add this group after the engine's:

```xml
  <!-- Editor only (MainframeEngine.Video: speech recognition for the video editor; never referenced by the runtime) -->
  <ItemGroup>
    <PackageVersion Include="Whisper.net" Version="[1.9.1]" />
    <PackageVersion Include="Whisper.net.Runtime" Version="[1.9.1]" />
  </ItemGroup>
```

In `MainframeEngine.Video.csproj`, add an `ItemGroup` with `<PackageReference Include="Whisper.net" />` and
`<PackageReference Include="Whisper.net.Runtime" />`.

In `ModelStore`, delete `CreateDefault()` and its comment; the editor passes `{user data}/models` (Task 13). Update any
test that used `CreateDefault`.

- [ ] **Step 2: Convert the tests and run them**

Use Task 1 Step 3's table and naming. Run `dotnet test Tests/MainframeEngine.Video.Tests`.
Expected: PASS, **424 + 27 = 451**. If any of the 27 tests covered the `NotImplemented*` types, subtract them and note
the number in the report.

Then check that Whisper's native runtimes are built. (Whether they land in the editor's output is checked once Task 4
references the library.) Run `dotnet build MainframeEngine.Video -c Release` and check that
`bin/Release/net10.0/runtimes/` holds the `whisper`/`ggml` native libraries for osx-arm64, win-x64 and linux-x64.

- [ ] **Step 3: Notices**

In the summary table of `THIRD_PARTY_NOTICES.md`, keeping its column style:

```
| Cutback (copied code) | 90cc508 | MIT | `MainframeEngine.Video` (`LICENSE-CUTBACK`) | editor only: `MainframeEngine.Video.dll` (video editing mode) |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | 1.9.1 | MIT | NuGet packages `Whisper.net`, `Whisper.net.Runtime` | editor only: `Whisper.net.dll` + native runtimes (speech recognition) |
| [whisper.cpp](https://github.com/ggml-org/whisper.cpp) | via Whisper.net.Runtime 1.9.1 | MIT | native libraries inside `Whisper.net.Runtime` | editor only: `whisper`/`ggml` native libraries |
```

Add the sections `## Cutback`, `## Whisper.net` and `## whisper.cpp`. Each has a one-sentence introduction and the
verbatim licence:
- **Cutback:** from `MainframeEngine.Video/LICENSE-CUTBACK`.
- **Whisper.net:** from the LICENSE inside `~/.nuget/packages/whisper.net/1.9.1/`. If the package carries none, quote
  the MIT licence from the project's repository and note which source was used in the report.
- **whisper.cpp:** its MIT licence as shipped in `whisper.net.runtime`, or the repository's if the package has none.

- [ ] **Step 4: Gates and commit**

```bash
just build && just format-check
git add MainframeEngine.Video Tests/MainframeEngine.Video.Tests Directory.Packages.props THIRD_PARTY_NOTICES.md
git commit -m "Port Cutback's speech recognition: Whisper transcription and the model store

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 4: Editor modes — `EditorMode`, the video layout, per-mode menus, the toolbar toggle, the tab filter

**Files:**
- Create: `MainframeEngine.Editor/Src/EditorMode.cs`; `MainframeEngine.Editor/Src/Video/VideoPanel.cs` (only the empty
  state in this task); `MainframeEngine.Editor/Assets/Editor/video.rml`.
- Modify:
  - `MainframeEngine.Editor/MainframeEngine.Editor.csproj`: a `ProjectReference` to `..\MainframeEngine.Video\MainframeEngine.Video.csproj`.
  - `MainframeEngine.Editor/Src/Session/IEditorTab.cs`: a default `Mode` member.
  - `MainframeEngine.Editor/Src/Session/EditorSession.cs`: `Add(IEditorTab)` and `Deactivate()`.
  - `MainframeEngine.Editor/Src/Layout/EditorLayout.cs`: the mode; a video branch in `Recompute`; four
    `EditorLayoutSettings` fields; `SplitterRect`.
  - `MainframeEngine.Editor/Src/EditorWorkspace.cs`: `Mode`, `SetMode`, `ModeChanged`, `VideoPanel`, `ApplyLayout`,
    `OnUnhandledInput`, `VideoShortcutFor`, and the auto-switch in `OnActiveChanged`.
  - `MainframeEngine.Editor/Src/EditorWorkspace.Projects.cs`: `StartUp` honours the saved mode; the project manager's
    Video Editor event.
  - `MainframeEngine.Editor/Src/EditorCommands.cs`: `MenuItems` per mode; `editor.mode.game`, `editor.mode.video`,
    `editor.video_editor`, `view.output`.
  - `MainframeEngine.Editor/Src/UI/Panels.cs`: `MenuBarPanel` (`ApplyMode`, the `CloseMenus` list), `ToolbarPanel`
    (`ApplyMode`, the mode toggle), and `SplittersOverlay.Apply` (hides zero-size handles).
  - `MainframeEngine.Editor/Assets/Editor/menubar.rml`: a `menu-video` title.
  - `MainframeEngine.Editor/Assets/Editor/toolbar.rml`: `#game-tools`, `#video-tools` and `#mode-toggle`.
  - `MainframeEngine.Editor/Src/UI/ViewportPanel.cs`: the tab filter, filtered indices, and "+" in video mode.
  - `MainframeEngine.Editor/Src/UI/ProjectManager.cs` and `Assets/Editor/project_manager.rml`: a "Video Editor"
    button.
  - `MainframeEngine.Editor/Src/Settings/EditorSettings.cs`: `LastMode`, with load, save and clone.
- Test:
  - `Tests/MainframeEngine.Editor.Tests/Video/EditorModeTests.cs` (new).
  - `WorkspaceTests.cs:121`: the panel count goes from 9 to 10.
  - `IconTests.cs:446-455`: add `"video"` to the menu list, and check every menu in both modes.
  - The editor goldens, re-recorded, because the toolbar gains the mode toggle.

**Interfaces:**
- Produces:
  ```csharp
  namespace MainframeEngine.Editor;
  public enum EditorMode { Game, Video }   // Audio (DAW) later
  // IEditorTab
  EditorMode Mode => EditorMode.Game;      // default interface member; VideoTab overrides with Video
  // EditorSession
  internal void Add(IEditorTab tab);       // adds, raises ScenesChanged, activates (OpenVideo in Task 10 uses it)
  public void Deactivate();                // no active tab: pauses every tab like Activate does for inactive ones
  // EditorWorkspace
  public EditorMode Mode { get; }
  public event Action? ModeChanged;
  public VideoPanel VideoPanel { get; }
  public void SetMode(EditorMode mode);
  public static string? VideoShortcutFor(Key key, EditorModifiers modifiers);
  // EditorLayout
  public EditorMode Mode { get; }
  public void SetMode(EditorMode mode);    // recomputes
  // EditorLayoutSettings: VideoShowOutput (false), VideoShowTranscript (true), VideoTranscriptWidth (320), VideoTimelineHeight (180)
  // EditorSettings: public EditorMode LastMode { get; set; }   (default Game; persisted as "lastMode": "video")
  ```

**Behaviour** (all tested in Step 1):

**`SetMode(mode)`** does nothing when `mode == Mode`. Otherwise:
1. Remember `Session.ActiveTab` for the old mode in a `Dictionary<EditorMode, IEditorTab>`.
2. Set `Mode`, and call `Layout.SetMode(mode)`.
3. Apply panel visibility:
   - **Game:** SceneTree, FileSystem, Inspector and Output are visible; VideoPanel is hidden with `HideAndReleaseFocus`.
   - **Video:** SceneTree, FileSystem and Inspector are hidden with `HideAndReleaseFocus`; Output is visible iff
     `Layout.Settings.VideoShowOutput`; VideoPanel is visible.
   - SongPanel needs nothing here: `SongView` already hides it whenever the active tab is not a `SongTab`.
4. Call `ApplyLayout()`, `MenuBar.ApplyMode(mode)`, `Toolbar.ApplyMode(mode)` and `ViewportPanel.RefreshTabs()`.
5. Activate the new mode's remembered tab if it is still open, else the first open tab of the new mode. If there is
   none, call `Session.Deactivate()`.
6. Persist the mode: `var s = Settings.Clone(); s.LastMode = mode; ApplySettings(s);`.
7. Call `UpdateTitleNow()` and raise `ModeChanged`.

**Auto-switch.** In the workspace's `OnActiveChanged` (or a new handler on `Session.ActiveChanged`): if
`Session.ActiveTab is { } t && t.Mode != Mode`, call `SetMode(t.Mode)`. In this case `SetMode` must keep `t` active
rather than activate the remembered tab, so use a private overload, `SetMode(mode, keepActive: true)`.

**Layout.** In `EditorLayout.Recompute`, game mode is unchanged. In video mode:
- `MenuBar` and `Toolbar` are as in game mode.
- `SceneTree`, `FileSystem` and `Inspector` are `default` (zero size).
- `bottom = VideoShowOutput ? Clamp(BottomHeight, MinBottom, Max(MinBottom, content - MinViewportHeight - SplitterSize)) : 0`.
- `Viewport = (0, top, w, Max(1, content - (VideoShowOutput ? bottom + SplitterSize : 0)))`.
- `Output = VideoShowOutput ? (0, top + Viewport.Height + SplitterSize, w, bottom) : default`.
- `ViewportImage` (the viewport minus its 30 dp tab strip) is the VideoPanel's rect in both modes.
- In `SplitterRect`, video mode has only `Bottom`, and only when `VideoShowOutput` is on. Every other splitter returns
  `default`.
- In `SplittersOverlay.Apply`, a handle whose rect has zero width or height gets `display: none`; the others get
  `display: block`.

**`ApplyLayout`** adds `VideoPanel.SetRect(Layout.ViewportImage)`.

**Menus.**
- `menubar.rml` gets `<div class="menu-title" id="menu-video" data-menu="video">Video</div>` after `menu-view`.
- `MenuBarPanel.ApplyMode` sets class `hidden` on `menu-project` and `menu-run` in video mode, and on `menu-video` in
  game mode. `CloseMenus` iterates all seven ids.
- `MenuItems(menu)` branches on `_workspace.Mode` for `file`, `edit` and `view`. `video` always returns the video menu.
- Every video id below is defined here; tasks 10–15 implement their handlers. Until then `Execute` returns false for
  an unknown id.

| Menu (video mode) | Items (label, `command`, shortcut, icon) |
|---|---|
| file | Open… `file.open` Ctrl+O `folder-open`; Header "Recent Videos" plus up to 5 items `video.recent:{i}` (Task 10 fills these; empty here); sep; Save `file.save` Ctrl+S `device-floppy`; Save As… `file.save_as` Ctrl+Shift+S `device-floppy`; sep; Export Video… `video.export` Ctrl+E `download`; Export Transcript… `video.export_transcript` `file-text`; sep; Close Tab `file.close` Ctrl+W `x`; Quit `file.quit` Ctrl+Q `logout` |
| edit | Undo `edit.undo` Ctrl+Z `arrow-back-up`; Redo `edit.redo` Ctrl+Shift+Z `arrow-forward-up`; sep; Delete Region `edit.delete` Del `trash`; sep; Video Settings… `video.settings` `settings` |
| view | Show/Hide Transcript `video.toggle_transcript` `file-text`; Show/Hide Output `view.output` `terminal`; sep; Switch to Game Mode `editor.mode.game` `device-gamepad-2` |
| video | Play/Pause `video.play` Space `player-play`; sep; Transcribe `video.transcribe` `microphone`; Remove Filler Words `video.remove_fillers` `eraser`; Detect Silence `video.detect_silence` `volume-off`; Detect Idle Gameplay `video.detect_idle` `device-gamepad-2` |

- Game mode's View menu gains `MenuItem.Separator` followed by "Switch to Video Mode" `editor.mode.video`, icon `video`.
- Every icon above must exist in `Assets/icons/icons.txt`, and `IconTests` fails if one does not. For any that is
  missing, use the closest existing name and note it in the report. Do not regenerate the atlas.
- Video items are enabled iff `Session.ActiveTab?.Mode == EditorMode.Video`. The exceptions, which are always enabled,
  are Open, the recents, Quit, Video Settings and the mode switch.

**Toolbar.**
- In `toolbar.rml`, wrap the existing groups and spacers before `#docs` in `<div id="game-tools" class="tools">`
  (styled `display: flex; flex-direction: row; align-items: center; flex: 1 1 auto;`). Add an empty
  `<div id="video-tools" class="tools hidden">`, which Task 11 fills.
- Before `#docs`, add:

  ```html
  <div id="mode-toggle"><button id="mode-game" class="mode" data-command="editor.mode.game" data-tooltip="Game mode: scenes, play and record">Game</button><button id="mode-video" class="mode" data-command="editor.mode.video" data-tooltip="Video mode: trim and export recordings">Video</button></div>
  ```

  Style it as a pill:
  `#mode-toggle { display: flex; height: 22dp; border-radius: 11dp; border: 1dp #3b3f4b; background-color: #121418; margin: 0dp 8dp; } button.mode { height: 20dp; padding: 0dp 12dp; border-radius: 10dp; background-color: transparent; color: #8b93a5; } button.mode.active { background-color: #1d4ed8; color: #ffffff; }`.
- `ToolbarPanel.ApplyMode(mode)` toggles `hidden` on `#game-tools`/`#video-tools` and `active` on
  `#mode-game`/`#mode-video`. `OnAttach` re-applies it after a hot reload.

**Tab strip.** In `ViewportPanel.RefreshTabs()`:
- Skip a tab when `tab.Mode != Workspace.Mode`.
- Set `Index = _tabs.Count`, the filtered position, so `TabAt` and the tab events stay right.
- The `create()` event runs `file.open` in video mode and `file.new` in game mode.
- Its tooltip binds to a string that changes with the mode: "Open a recording (Ctrl+O)" in video mode.

**Shortcuts.**
- In video mode, `OnUnhandledInput` skips `SongView.HandleKey` and `ProjectShortcutFor`, and uses
  `VideoShortcutFor(key, mods)` in place of `ShortcutFor`. Task 11 inserts `VideoView.HandleKey` before it.
- `VideoShortcutFor` maps:
  - Cmd/Ctrl+O → `file.open`;
  - Cmd+S → `file.save`, and Cmd+Shift+S → `file.save_as`;
  - Cmd+W → `file.close`, and Cmd+Q → `file.quit`;
  - Cmd+Z → `edit.undo`, and Cmd+Shift+Z or Cmd+Y → `edit.redo`;
  - Cmd+E → `video.export`;
  - Delete or Backspace with no modifiers → `edit.delete`;
  - F1 → `help.docs`;
  - anything else → null.

**Start-up and the project manager.**
- `StartUp()`: when `Settings.LastMode == Video` and there is no initial scene or project, skip
  `ProjectManager.Open()` and call `SetMode(EditorMode.Video)`.
- Add a "Video Editor" button to the project manager's `.pm-actions`, after `#pm-remove`:

  ```html
  <button id="pm-video" data-event-click="video()"><span class="icon icon-video"></span><span>Video Editor</span></button>
  ```

  Wire it with `.Event("video", _ => Workspace.Commands.Execute("editor.video_editor"))`, which calls
  `ProjectManager.Close()` then `SetMode(Video)`.

**Settings.** `EditorSettings.LastMode` follows the `ViewResolution` pattern: an enum stored as a camelCase string, with
an invalid or missing value read as `Game`. Map it in `Load`, `Save` and `Clone`.

**The empty VideoPanel.**
- `video.rml` is `<body class="panel-doc video-doc" data-model="video">`, holding a centred `#video-empty` block with
  an `icon-video` icon, the text "Open a recording (File › Open…) or record one in Game mode (F10)", and an empty
  `#video-recent` list that Task 10 fills.
- `VideoPanel : EditorDocument` (constructor `base(workspace, "video.rml")`, with `Visible = false`) is added to
  `PanelLayer` right after `SongPanel`. It then draws above the viewport and below the menus.

- [ ] **Step 1: Write the failing tests**

`Tests/MainframeEngine.Editor.Tests/Video/EditorModeTests.cs`:

```csharp
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>A minimal video-mode tab for mode tests before VideoTab exists (Task 10 adds the real one).</summary>
internal sealed class FakeVideoTab(string name) : IEditorTab
{
    public string? FilePath => null;
    public string DisplayName => name;
    public string Title => name;
    public bool IsDirty => false;
    public string IconClasses => "icon icon-video";
    public string Tooltip => name;
    public EditorMode Mode => EditorMode.Video;
    public void Dispose() { }
}

[Collection(nameof(SerialEditor))]
public sealed class EditorModeTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    private EditorWorkspace W => _editor.Workspace;

    public void Dispose() => _editor.Dispose();

    [Fact]
    public void TheEditorStartsInGameMode()
    {
        Assert.Equal(EditorMode.Game, W.Mode);
        Assert.True(W.SceneTree.Visible);
        Assert.False(W.VideoPanel.Visible);
    }

    [Fact]
    public void VideoModeHidesTheGamePanelsAndShowsTheVideoPanel()
    {
        W.SetMode(EditorMode.Video);
        _editor.Tick();
        Assert.False(W.SceneTree.Visible);
        Assert.False(W.FileSystem.Visible);
        Assert.False(W.Inspector.Visible);
        Assert.False(W.OutputPanel.Visible);
        Assert.True(W.VideoPanel.Visible);
        Assert.Equal(default, W.Layout.SceneTree);
        Assert.Equal(0, W.Layout.Viewport.X);
        Assert.Equal(W.Layout.MenuBar.Width, W.Layout.Viewport.Width);
    }

    [Fact]
    public void SwitchingBackRestoresTheGameLayout()
    {
        var before = W.Layout.SceneTree;
        W.SetMode(EditorMode.Video);
        W.SetMode(EditorMode.Game);
        Assert.Equal(before, W.Layout.SceneTree);
        Assert.True(W.SceneTree.Visible);
        Assert.False(W.VideoPanel.Visible);
    }

    [Fact]
    public void VideoModeHasNoActiveTabWithoutAVideoTab()
    {
        W.SetMode(EditorMode.Video);
        Assert.Null(W.Session.ActiveTab);
        Assert.Empty(W.ViewportPanel.VisibleTabs); // expose the filtered list as IReadOnlyList<IEditorTab> VisibleTabs
    }

    [Fact]
    public void ActivatingAVideoTabSwitchesToVideoMode()
    {
        var video = new FakeVideoTab("run.mfcut");
        W.Session.Add(video);
        Assert.Equal(EditorMode.Video, W.Mode);
        Assert.Same(video, W.Session.ActiveTab);
    }

    [Fact]
    public void SwitchingModesKeepsEachModesActiveTab()
    {
        var scene = W.Session.ActiveTab!;
        var video = new FakeVideoTab("run.mfcut");
        W.Session.Add(video);
        W.SetMode(EditorMode.Game);
        Assert.Same(scene, W.Session.ActiveTab);
        W.SetMode(EditorMode.Video);
        Assert.Same(video, W.Session.ActiveTab);
    }

    [Fact]
    public void TheTabStripShowsOnlyTheModesTabs()
    {
        W.Session.NewScene();
        W.Session.Add(new FakeVideoTab("run.mfcut"));
        Assert.Single(W.ViewportPanel.VisibleTabs);
        W.SetMode(EditorMode.Game);
        Assert.Equal(2, W.ViewportPanel.VisibleTabs.Count);
    }

    [Fact]
    public void ClosingAFilteredTabClosesThatTab()
    {
        var video = new FakeVideoTab("run.mfcut");
        W.Session.Add(video);
        W.ViewportPanel.CloseAt(0); // the first *visible* tab: the video tab, not the session's first (a scene)
        Assert.DoesNotContain(video, W.Session.Tabs);
        Assert.Single(W.Session.Tabs);
    }

    [Fact]
    public void TheMenusFollowTheMode()
    {
        Assert.Contains(W.Commands.MenuItems("file"), i => i.Command == "file.new");
        W.SetMode(EditorMode.Video);
        var file = W.Commands.MenuItems("file");
        Assert.Contains(file, i => i.Command == "video.export");
        Assert.DoesNotContain(file, i => i.Command == "file.new");
        Assert.Contains(W.Commands.MenuItems("video"), i => i.Command == "video.transcribe");
    }

    [Fact]
    public void TheModeIsSavedAndRestoredAtStartUp()
    {
        var settings = Path.Combine(_editor.Directory, "settings.json");
        new EditorSettings { LastMode = EditorMode.Video }.Save(settings);
        Assert.Equal(EditorMode.Video, EditorSettings.Load(settings).LastMode);

        using var editor = new HeadlessEditor(configure: o => o with { EditorSettingsPath = settings, ShowProjectManager = true });
        Assert.Equal(EditorMode.Video, editor.Workspace.Mode);
        Assert.False(editor.Workspace.ProjectManager.Visible);
    }

    [Fact]
    public void TheProjectManagerOpensTheVideoEditorWithoutAProject()
    {
        using var editor = new HeadlessEditor(configure: o => o with { ShowProjectManager = true });
        Assert.True(editor.Workspace.ProjectManager.Visible);
        editor.Workspace.Commands.Execute("editor.video_editor");
        Assert.False(editor.Workspace.ProjectManager.Visible);
        Assert.Equal(EditorMode.Video, editor.Workspace.Mode);
    }

    [Theory]
    [InlineData(Key.O, EditorModifiers.Command, "file.open")]
    [InlineData(Key.S, EditorModifiers.Command | EditorModifiers.Shift, "file.save_as")]
    [InlineData(Key.Z, EditorModifiers.Command | EditorModifiers.Shift, "edit.redo")]
    [InlineData(Key.E, EditorModifiers.Command, "video.export")]
    [InlineData(Key.Backspace, EditorModifiers.None, "edit.delete")]
    [InlineData(Key.W, EditorModifiers.None, null)]   // the gizmo keys are game-only
    [InlineData(Key.F5, EditorModifiers.None, null)]  // so is Play
    public void VideoShortcuts(Key key, EditorModifiers modifiers, string? command) =>
        Assert.Equal(command, EditorWorkspace.VideoShortcutFor(key, modifiers));
}
```

`ViewportPanel` gains `public IReadOnlyList<IEditorTab> VisibleTabs` (the filtered `Item`s) and
`internal void CloseAt(int index)`, which is what the `close(i)` event calls. Read `HeadlessEditor` before using
`configure`: the option names are `EditorSettingsPath` and `ShowProjectManager` (on `EditorWorkspaceOptions`).

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter FullyQualifiedName~EditorModeTests`
Expected: the build fails.

- [ ] **Step 2: Implement the behaviour above**, keeping each change in the file named under **Files**.

- [ ] **Step 3: Update the existing tests**

- `WorkspaceTests.cs:121`: `Assert.Equal(10, W.PanelLayer.Documents.Count); // with the song and video panels`.
- `IconTests.EveryMenuItemHasAnIcon`: iterate `{"file","edit","view","project","run","help","video"}` in game mode,
  call `W.SetMode(EditorMode.Video)`, then iterate them again.
- `IconTests.Lint`: run it in both modes.

Run: `dotnet test Tests/MainframeEngine.Editor.Tests`
Expected: PASS, with 18 more tests than before (11 facts, plus a theory with 7 cases).

- [ ] **Step 4: Re-record the editor goldens** (the toolbar gained the mode toggle)

```bash
UPDATE_GOLDENS=1 dotnet test Tests/MainframeEngine.RenderTests --filter "FullyQualifiedName~EditorRenderTests"
```

Open each changed MoltenVK PNG and check that the toggle sits at the toolbar's right edge and that nothing else moved
unexpectedly. Then record the lavapipe goldens with Docker: run
`just render-tests-linux --filter FullyQualifiedName~EditorRenderTests`, then copy the
`artifacts/linux/render-tests/new-goldens/lavapipe/editor-*.png` files it reports into
`Tests/MainframeEngine.RenderTests/Goldens/lavapipe/`.

If Docker is not running, stop and report NEEDS_CONTEXT, so the controller can ask the user to start Docker Desktop.
Never copy MoltenVK images into the lavapipe folder.

- [ ] **Step 5: Gates and commit**

```bash
just build && just format-check && just test-render
git add MainframeEngine.Editor Tests docs
git commit -m "Add editor modes: a Game|Video toggle that swaps the layout, menus, toolbar and tabs

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: `VideoDocument` — open, save, detect, and the transcript operations

**Files:**
- Create: `MainframeEngine.Editor/Src/Video/VideoDocument.cs` and `MainframeEngine.Editor/Src/Video/VideoFormat.cs`.
- Test: `Tests/MainframeEngine.Editor.Tests/Video/VideoDocumentTests.cs`.

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces:
  ```csharp
  namespace MainframeEngine.Editor.Video;
  public sealed class VideoDocument
  {
      public const int DefaultHistoryLimit = 100;
      public const double BoundarySnapSeconds = 0.020, WordSnapSeconds = 0.040, EnvelopeBucketSeconds = 0.010;
      public static VideoDocument FromProject(VideoProject project, string? projectPath, CaptureSidecar? capture, IReadOnlyList<string> warnings, int historyLimit = DefaultHistoryLimit, bool legacy = false);
      public static Task<VideoDocument> OpenAsync(string path, FfmpegLocation ffmpeg, int historyLimit, CancellationToken cancellationToken);
      public VideoProject Project { get; }
      public string? FilePath { get; }            // the .mfcut; null until saved (a new recording, or an imported .cutback)
      public string SourcePath { get; }
      public double Duration { get; }
      public SegmentList Segments { get; }
      public SegmentEditor Editor { get; }
      public CaptureSidecar? Capture { get; }
      public Waveform? Waveform { get; set; }
      public IReadOnlyList<Word> Transcript { get; }
      public bool TranscriptHasAnchors { get; }
      public IReadOnlyList<string> Warnings { get; }
      public int Version { get; }                 // +1 per partition or transcript change
      public bool IsDirty { get; }                // Editor.IsDirty || transcript changed since save || unsaved import
      public event Action? Changed;
      public event Action? DirtyChanged;
      public string DefaultProjectPath { get; }   // <video dir>/<video name>.mfcut, or <legacy name>.mfcut
      public void Save(string? path = null);
      public string DetectIdleGameplay(GameplaySettings settings);
      public string ApplySilence(IReadOnlyList<SilenceSpan> spans, DetectionSettings settings);
      public void SetTranscript(IReadOnlyList<Word> words);
      public (double Start, double End) SnapWordSpan(double start, double end);
      public bool CutWords(int first, int last, int anchor);
      public string RemoveFillerWords(IReadOnlyList<string> fillerWords, DetectionSettings settings);
      public string TranscriptText(bool srt);
      public double SnapTime(double time);
  }
  public static class VideoFormat { public static string Clock(double seconds); public static string Ruler(double seconds); }
  ```

**Porting notes** (where each body comes from):
- **`OpenAsync`** ports Cutback's `MainWindowViewModel.OpenVideoAsync` / `OpenProjectAsync` / `RunOpenAsync`, without
  the UI state and without waveform extraction (the tab starts that as a job in Task 10).
  - **A `.mfcut` or `.cutback`:** load it with `ProjectSerializer.LoadAsync`.
    - If the source is missing, throw `FileNotFoundException("The recording this project trims is missing: {path}")`.
    - If `SourceHash.MatchesAsync` fails, add the warning
      `"{name} has changed since this project was saved; check the cuts."`.
    - A `.cutback` opens with `legacy: true` and `projectPath: null`.
  - **A video:**
    - Probe it with `new MediaProbe(ffmpeg).ProbeAsync`.
    - With no audio, throw `InvalidOperationException("{name} has no audio track, so it cannot be trimmed here.")`.
    - Then call `SourceHash.ComputeAsync` and `VideoProject.CreateNew`.
  - **Either way:** call `CaptureSidecarSerializer.TryLoadForVideoAsync`. A `CaptureSidecarFormatException`,
    `JsonException`, `IOException` or `UnauthorizedAccessException` becomes the warning
    `"The recording's sidecar could not be read ({message}); Detect Idle Gameplay is off."`.
  - When the loaded transcript has words but no `Anchor`, `TranscriptHasAnchors` is false.
- **`DetectIdleGameplay`** ports Cutback's `DetectIdleGameplay`. **`ApplySilence`** is the second half of
  `DetectSilenceAsync` (after the detector), plus `Project = Project with { Settings = settings }`. Both keep Cutback's
  status wording, using `VideoFormat.Clock` in place of `TimeFormat.Clock`.
- **`SnapWordSpan`**: with no waveform, return `(start, end)`. Otherwise snap both edges with
  `SnapToZeroCrossing(t, WordSnapSeconds)`, and return the raw edges if the snapped span is shorter than 0.02 s.
- **`CutWords`** ports `CutWords`:
  - clamp `first` and `last` to the transcript and put them in order;
  - clamp `anchor` into `[first, last]`;
  - `enabled = TranscriptView.IsCut(Transcript[anchor], Segments.Segments)`;
  - snap the span, then make one `Editor.Edit(s => s.SetRange(start, end, enabled))` step.
- **`RemoveFillerWords`** ports `RemoveFillerWordsAsync` and `LocateFillerSpan`, without the transcribe-first branch
  (the caller does that):
  1. `FillerDetector.Find`.
  2. Locate each filler with `Waveform.Envelope(EnvelopeBucketSeconds)` and `FillerSpanLocator`, falling back to the
     snapped span.
  3. `FillerCutPlanner.Plan`.
  4. Apply the cuts as one step: `Editor.Edit(s => s.ApplyFillerCuts(cuts))`.

  It keeps Cutback's four status messages and the "Transcribe again for cuts aligned to the audio." suffix.
- **`TranscriptText`** returns `TranscriptExporter.ToSrt` or `ToPlainText` over the current segments.
- **`Save`** writes `Project with { Segments = …, Transcript = Transcript }` atomically through
  `ProjectSerializer.SaveAsync(...).GetAwaiter().GetResult()`.
  - It adds `.mfcut` when the path has no extension.
  - It clears the transcript and legacy dirt, and calls `Editor.MarkSaved()`.
  - It throws `InvalidOperationException` when the document is unnamed and `path` is null.
- **`IsDirty` and `DirtyChanged`**: recompute the combined flag whenever `Editor.DirtyChanged` fires, the transcript
  changes or the document saves. Raise `DirtyChanged` only when the flag flips.

- [ ] **Step 1: Write the failing tests**

`Tests/MainframeEngine.Editor.Tests/Video/VideoDocumentTests.cs` (fixtures are inline, and nothing runs ffmpeg):

```csharp
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Projects;
using VideoCapture = MainframeEngine.Video.Capture;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class VideoDocumentTests : IDisposable
{
    private static readonly FfmpegLocation NoFfmpeg = new("/x/ffmpeg", "/x/ffprobe", "/x", FfmpegLocationSource.Path);
    private readonly string _dir = Directory.CreateTempSubdirectory("video-doc-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    internal static VideoProject Project(string sourcePath, double duration = 60) =>
        VideoProject.CreateNew(new SourceInfo(sourcePath, new string('0', 64), duration, 1920, 1080, 60));

    internal const string Sidecar = """
        { "format": "mainframe-capture", "version": 1, "video": "a.mp4", "engineVersion": "0.42.0", "game": "Demo",
          "scene": "res://Scenes/Main.mscene", "mode": "live", "frameRate": 60, "width": 1920, "height": 1080,
          "durationSeconds": 60.0, "droppedFrames": 0,
          "input": [ { "start": 0.0, "end": 14.0 }, { "start": 20.0, "end": 60.0 } ],
          "loading": [ { "start": 14.5, "end": 19.0 } ], "paused": [], "keep": [], "skip": [],
          "markers": [ { "time": 30.0, "label": "boss" } ] }
        """;

    // so · um · we · go — "um" is a filler; words carry DTW anchors
    internal static Word[] Words() =>
    [
        new("so", 1.0, 1.3, 0.9, 1.1), new("um", 1.4, 1.8, 0.8, 1.5), new("we", 2.0, 2.2, 0.9, 2.05), new("go", 2.3, 2.6, 0.9, 2.4),
    ];

    private VideoDocument Doc(VideoCapture.CaptureSidecar? capture = null) =>
        VideoDocument.FromProject(Project(Path.Combine(_dir, "a.mp4")), null, capture, []);

    [Fact]
    public void ANewDocumentIsCleanAndUnnamed()
    {
        var doc = Doc();
        Assert.False(doc.IsDirty);
        Assert.Null(doc.FilePath);
        Assert.Equal(Path.Combine(_dir, "a.mfcut"), doc.DefaultProjectPath);
        Assert.Single(doc.Segments.Segments);
    }

    [Fact]
    public void SaveWritesTheProjectFormatAndAddsTheExtension()
    {
        var doc = Doc();
        doc.Editor.CreateSection(10, 20, anchor: 10);
        doc.Save(Path.Combine(_dir, "trim"));
        var path = Path.Combine(_dir, "trim.mfcut");
        Assert.Equal(path, doc.FilePath);
        Assert.False(doc.IsDirty);
        Assert.Equal(ProjectSerializer.Serialize(doc.Project), File.ReadAllText(path));
        Assert.Equal(3, doc.Project.Segments.Count);
    }

    [Fact]
    public async Task ASavedProjectReopensIdentically()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        doc.Editor.CreateSection(5, 7, anchor: 5);
        doc.Save(Path.Combine(_dir, "a.mfcut"));
        var loaded = await ProjectSerializer.LoadAsync(Path.Combine(_dir, "a.mfcut"), CancellationToken.None);
        Assert.Equal(doc.Segments.Segments, loaded.Segments);
        Assert.Equal(Words(), loaded.Transcript);
    }

    [Fact]
    public void SavingAnUnnamedDocumentWithoutAPathThrows() => Assert.Throws<InvalidOperationException>(() => Doc().Save());

    [Fact]
    public void DetectIdleGameplayCutsTheLoadingScreenAsOneUndoStep()
    {
        var doc = Doc(VideoCapture.CaptureSidecarSerializer.Deserialize(Sidecar));
        Assert.StartsWith("Cut ", doc.DetectIdleGameplay(GameplaySettings.Default), StringComparison.Ordinal);
        Assert.Contains(doc.Segments.Segments, s => !s.Enabled && s.Origin == SegmentOrigin.Gameplay && s.Start <= 14.5 && s.End >= 19.0);
        Assert.True(doc.Editor.Undo());
        Assert.Single(doc.Segments.Segments);
    }

    [Fact]
    public void NoSidecarMeansNoGameplayDetection() =>
        Assert.Throws<InvalidOperationException>(() => Doc().DetectIdleGameplay(GameplaySettings.Default));

    [Fact]
    public void ApplySilenceCutsPaddedSpansAndReportsThem()
    {
        var doc = Doc();
        Assert.Equal("Detected 2 silences, 0:02.8 removed.",
            doc.ApplySilence([new SilenceSpan(10, 12), new SilenceSpan(30, 31)], DetectionSettings.Default));
        Assert.Equal(5, doc.Segments.Count);
    }

    [Fact]
    public void SettingATranscriptMakesTheDocumentDirtyUntilSaved()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        Assert.True(doc.IsDirty);
        Assert.True(doc.TranscriptHasAnchors);
        doc.Save(Path.Combine(_dir, "a.mfcut"));
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void CuttingWordsIsOneManualUndoStep()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        Assert.True(doc.CutWords(1, 1, anchor: 1)); // "um"
        var cut = doc.Segments.Segments[doc.Segments.IndexAt(1.6)];
        Assert.False(cut.Enabled);
        Assert.Equal(SegmentOrigin.Manual, cut.Origin);
        Assert.Equal(1.4, cut.Start, 6); // no waveform: the raw word edges
        Assert.Equal(1.8, cut.End, 6);
        Assert.True(doc.Editor.Undo());
        Assert.Single(doc.Segments.Segments);
    }

    [Fact]
    public void ClickingACutWordRestoresIt()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        doc.CutWords(1, 1, 1);
        doc.CutWords(1, 1, 1);
        Assert.True(doc.Segments.Segments[doc.Segments.IndexAt(1.6)].Enabled);
    }

    [Fact]
    public void FillerRemovalCutsTheFillerAsOneStep()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        var status = doc.RemoveFillerWords(FillerDetector.DefaultWords, DetectionSettings.Default);
        Assert.StartsWith("Removed 1 filler word", status, StringComparison.Ordinal);
        Assert.Contains(doc.Segments.Segments, s => !s.Enabled && s.Origin == SegmentOrigin.Filler);
        Assert.True(doc.Editor.Undo());
        Assert.DoesNotContain(doc.Segments.Segments, s => s.Origin == SegmentOrigin.Filler);
    }

    [Fact]
    public void FillerRemovalLeavesManualEditsAlone()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        doc.Editor.CreateSection(1.3, 1.9, anchor: 1.3); // the user cut around "um" by hand…
        doc.Editor.CreateSection(1.3, 1.9, anchor: 1.5); // …then restored it
        doc.RemoveFillerWords(FillerDetector.DefaultWords, DetectionSettings.Default);
        Assert.True(doc.Segments.Segments[doc.Segments.IndexAt(1.6)].Enabled); // manual beats filler
    }

    [Fact]
    public void TheExportedTranscriptOmitsCutWords()
    {
        var doc = Doc();
        doc.SetTranscript(Words());
        doc.CutWords(1, 1, 1);
        Assert.DoesNotContain("um", doc.TranscriptText(srt: false), StringComparison.Ordinal);
        Assert.Contains("-->", doc.TranscriptText(srt: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProjectWhoseSourceIsMissingIsRefused()
    {
        var path = Path.Combine(_dir, "gone.mfcut");
        await ProjectSerializer.SaveAsync(Project(Path.Combine(_dir, "missing.mp4")), path, CancellationToken.None);
        var e = await Assert.ThrowsAsync<FileNotFoundException>(() => VideoDocument.OpenAsync(path, NoFfmpeg, 100, CancellationToken.None));
        Assert.Contains("missing.mp4", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangedSourceOpensWithAWarning()
    {
        var video = Path.Combine(_dir, "a.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3]);
        var path = Path.Combine(_dir, "a.mfcut");
        await ProjectSerializer.SaveAsync(Project(video), path, CancellationToken.None); // the zero hash never matches
        var doc = await VideoDocument.OpenAsync(path, NoFfmpeg, 100, CancellationToken.None);
        Assert.Contains(doc.Warnings, w => w.Contains("changed", StringComparison.Ordinal));
        Assert.Equal(path, doc.FilePath);
    }

    [Fact]
    public async Task ABadSidecarOpensWithAWarning()
    {
        var video = Path.Combine(_dir, "a.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3]);
        await File.WriteAllTextAsync(VideoCapture.CaptureSidecar.PathFor(video), "{ not json");
        var path = Path.Combine(_dir, "a.mfcut");
        await ProjectSerializer.SaveAsync(Project(video), path, CancellationToken.None);
        var doc = await VideoDocument.OpenAsync(path, NoFfmpeg, 100, CancellationToken.None);
        Assert.Null(doc.Capture);
        Assert.Contains(doc.Warnings, w => w.Contains("sidecar", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ALegacyProjectSavesAsMfcutNextToIt()
    {
        var video = Path.Combine(_dir, "a.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3]);
        var legacy = Path.Combine(_dir, "old.cutback");
        await ProjectSerializer.SaveAsync(Project(video), legacy, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(legacy);

        var doc = await VideoDocument.OpenAsync(legacy, NoFfmpeg, 100, CancellationToken.None);
        Assert.Null(doc.FilePath);
        Assert.True(doc.IsDirty);
        Assert.Equal(Path.Combine(_dir, "old.mfcut"), doc.DefaultProjectPath);
        doc.Save(doc.DefaultProjectPath);
        Assert.True(File.Exists(Path.Combine(_dir, "old.mfcut")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(legacy)); // never overwritten
    }

    [Theory]
    [InlineData(0.0, "0:00.0")]
    [InlineData(2.84, "0:02.8")]
    [InlineData(65.31, "1:05.3")]
    [InlineData(3725.0, "1:02:05.0")]
    public void ClockFormatsMinutesSecondsAndTenths(double seconds, string expected) => Assert.Equal(expected, VideoFormat.Clock(seconds));

    [Theory]
    [InlineData(0.0, "0:00")]
    [InlineData(75.0, "1:15")]
    [InlineData(3725.0, "1:02:05")]
    public void RulerLabelsAreWholeSeconds(double seconds, string expected) => Assert.Equal(expected, VideoFormat.Ruler(seconds));
}
```

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter FullyQualifiedName~VideoDocumentTests`
Expected: the build fails.

- [ ] **Step 2: Implement `VideoFormat` and `VideoDocument`** from the interfaces and porting notes above.
  `VideoFormat.Clock`:

```csharp
public static string Clock(double seconds)
{
    var tenths = (long)Math.Round(Math.Max(0, seconds) * 10, MidpointRounding.AwayFromZero);
    var (t, s, m, h) = (tenths % 10, tenths / 10 % 60, tenths / 600 % 60, tenths / 36000);
    return h > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}.{t}")
        : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}.{t}");
}
```

`Ruler` works the same way at whole seconds: `m:ss`, or `h:mm:ss` from an hour.

- [ ] **Step 3: Run the tests.** Expected: PASS, 24 tests.

- [ ] **Step 4: Gates and commit.** Message: "Add VideoDocument: open, save, detect and edit by transcript", with the
  trailer.

---

### Task 6: `EditorCanvas`, `VideoTimelineView` and `VideoTimelineCanvas`

**Files:**
- Create: `MainframeEngine.Editor/Src/UI/EditorCanvas.cs`, holding the dp drawing helpers moved out of `SongCanvas`.
- Modify: `MainframeEngine.Editor/Src/Music/SongCanvases.cs`: `SongCanvas : EditorCanvas`, and delete the moved members.
- Create: `MainframeEngine.Editor/Src/Video/VideoTimelineView.cs`, `VideoCanvasState.cs` and `VideoTimelineCanvas.cs`.
- Test: `Tests/MainframeEngine.Editor.Tests/Video/VideoTimelineViewTests.cs` and `VideoTimelineCanvasTests.cs`.

**Interfaces:**
- Consumes: `VideoDocument` (Task 5), `Waveform.LevelFor`, `WaveformLevel.Peaks`/`SecondsPerBucket`,
  `CaptureSidecar.Markers`, `VideoFormat.Ruler`.
- Produces:
  ```csharp
  namespace MainframeEngine.Editor;
  [Tool] public abstract class EditorCanvas : Node2D
  {
      public float PixelScale { get; set; } = 1;  public Vector2 ViewSize { get; set; } = new(800, 300);
      protected static Font? TextFont { get; }
      protected void Box(float x, float y, float w, float h, Vector4 color);
      protected void Outline(float x, float y, float w, float h, Vector4 color, float width = 1);
      protected void Line(float x0, float y0, float x1, float y1, Vector4 color, float width = 1);
      protected void Text(string text, float x, float baseline, float size, Vector4 color);
      protected static Vector4 Rgb(int r, int g, int b, float a = 1);  protected static Vector4 WithAlpha(Vector4 c, float a);
  }
  namespace MainframeEngine.Editor.Video;
  public sealed class VideoTimelineView { /* exactly as below */ }
  public sealed class VideoCanvasState
  {
      public int HoverSegment { get; set; } = -1;
      public int HoverBoundary { get; set; } = -1;
      public int DragBoundary { get; set; } = -1;
      public (double Start, double End)? PendingSection { get; set; }
      public double Playhead { get; set; }
  }
  [Tool] public sealed class VideoTimelineCanvas : EditorCanvas
  {
      public VideoTimelineCanvas(VideoDocument document, VideoTimelineView view, VideoCanvasState state);
      public static (int First, int Last) BucketsForColumn(double viewStart, double secondsPerPixel, double secondsPerBucket, int column, int bucketCount);
  }
  ```

- [ ] **Step 1: Extract `EditorCanvas`**

Move `_font`, `_fontLoaded`, `PixelScale`, `ViewSize`, `TextFont`, `Box`, `Outline`, `Line`, `Text`, `Rgb` and
`WithAlpha` from `SongCanvas` (`SongCanvases.cs:14-82`) into `EditorCanvas`, declared in namespace
`MainframeEngine.Editor` as `[Tool] public abstract class EditorCanvas : Node2D`.

Its summary: "Base of the editor's canvas views drawn into SubViewports (song, video). The helpers take dp and draw in
framebuffer pixels (PixelScale), so text stays sharp on HiDPI screens."

`SongCanvas` becomes `public abstract class SongCanvas : EditorCanvas` and keeps everything song-specific.

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter FullyQualifiedName~Music`
Expected: PASS, unchanged.

- [ ] **Step 2: Write the failing tests**

`VideoTimelineViewTests.cs`:

```csharp
using MainframeEngine.Editor.Video;
using MainframeEngine.Video;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class VideoTimelineViewTests
{
    private static VideoTimelineView View(double duration = 100, float width = 1000)
    {
        var view = new VideoTimelineView { Width = width };
        view.Reset(duration);
        return view;
    }

    private static SegmentList ThreeSegments() => new(10,
    [
        Segment.Create(0, 4, true, SegmentOrigin.Auto),
        Segment.Create(4, 6, false, SegmentOrigin.Auto),
        Segment.Create(6, 10, true, SegmentOrigin.Auto),
    ]);

    [Fact]
    public void ResetShowsTheWholeRecording()
    {
        var view = View();
        Assert.Equal(0, view.Start);
        Assert.Equal(100, view.Span);
        Assert.Equal(500, view.TimeToX(50), 6);
        Assert.Equal(50, view.XToTime(500), 6);
        Assert.Equal(0.1, view.SecondsPerPixel, 9);
    }

    [Fact]
    public void SetViewClampsTheSpanAndTheStart()
    {
        var view = View();
        view.SetView(-5, 0.01);
        Assert.Equal((0.0, 0.25), (view.Start, view.Span));
        view.SetView(99, 10);
        Assert.Equal(90, view.Start);
        view.SetView(0, 500);
        Assert.Equal(100, view.Span);
    }

    [Fact]
    public void TheMinimumSpanNeverExceedsAShortRecording()
    {
        var view = View(duration: 0.1);
        view.SetView(0, 0.01);
        Assert.Equal(0.1, view.Span, 9);
    }

    [Fact]
    public void WheelZoomKeepsTheTimeUnderTheCursor()
    {
        var view = View();
        view.ZoomAbout(500, wheelDeltaY: 1);
        Assert.Equal(80, view.Span, 6);
        Assert.Equal(10, view.Start, 6);
        Assert.Equal(50, view.XToTime(500), 6);
    }

    [Fact]
    public void PanStaysWithinTheRecording()
    {
        var view = View();
        view.SetView(20, 10);
        view.Pan(-30);
        Assert.Equal(0, view.Start);
    }

    [Fact]
    public void KeepVisibleRecentresOnlyWhenThePlayheadLeavesTheView()
    {
        var view = View();
        view.SetView(0, 10);
        Assert.False(view.KeepVisible(5));
        Assert.True(view.KeepVisible(15));
        Assert.Equal(14, view.Start, 6); // 15 − 0.1 × 10
    }

    [Fact]
    public void EveryViewChangeBumpsTheVersion()
    {
        var view = View();
        var version = view.Version;
        view.SetView(10, 10);
        Assert.True(view.Version > version);
    }

    [Fact]
    public void BoundaryHitsNeedToBeBelowTheRulerAndWithinSixDp()
    {
        var segments = ThreeSegments();
        var view = View(duration: 10); // 100 dp per second: boundaries at x 400 and 600
        Assert.Equal(1, view.BoundaryAt(segments.Segments, 404, 50));
        Assert.Equal(-1, view.BoundaryAt(segments.Segments, 406, 50));
        Assert.Equal(2, view.BoundaryAt(segments.Segments, 597, 50));
        Assert.Equal(-1, view.BoundaryAt(segments.Segments, 400, 10));
    }

    [Fact]
    public void SegmentHitsUseTheTimeUnderThePointer()
    {
        var segments = ThreeSegments();
        var view = View(duration: 10);
        Assert.Equal(0, view.SegmentAt(segments, 100, 50));
        Assert.Equal(1, view.SegmentAt(segments, 500, 50));
        Assert.Equal(2, view.SegmentAt(segments, 999, 50));
        Assert.Equal(-1, view.SegmentAt(segments, 500, 10));
        Assert.Equal(-1, view.SegmentAt(segments, 1001, 50));
    }

    [Theory]
    [InlineData(1000.0, 0.1, 0.02)]
    [InlineData(10.0, 10.0, 2.0)]
    [InlineData(1.0, 120.0, 30.0)]
    [InlineData(0.01, 3600.0, 900.0)]
    public void RulerLabelsAreAtLeast80DpApart(double pxPerSecond, double major, double minor)
    {
        var (ma, mi) = VideoTimelineView.RulerIntervals(pxPerSecond);
        Assert.Equal(major, ma, 9);
        Assert.Equal(minor, mi, 9);
    }
}
```

`VideoTimelineCanvasTests.cs`:

```csharp
using MainframeEngine.Editor.Video;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class VideoTimelineCanvasTests
{
    [Fact]
    public void AColumnCoversTheBucketsUnderItsPixel()
    {
        // 0.01 s per pixel and 0.002 s per bucket: five buckets per column
        Assert.Equal((0, 4), VideoTimelineCanvas.BucketsForColumn(0, 0.01, 0.002, column: 0, bucketCount: 1000));
        Assert.Equal((50, 54), VideoTimelineCanvas.BucketsForColumn(0, 0.01, 0.002, column: 10, bucketCount: 1000));
    }

    [Fact]
    public void ColumnsPastTheEndAreEmptyAndAColumnSpansAtLeastOneBucket()
    {
        Assert.Equal((-1, -1), VideoTimelineCanvas.BucketsForColumn(0, 0.01, 0.002, column: 500, bucketCount: 1000));
        Assert.Equal((5, 5), VideoTimelineCanvas.BucketsForColumn(0, 0.001, 0.002, column: 10, bucketCount: 1000));
    }
}
```

- [ ] **Step 3: Implement `VideoTimelineView`**

```csharp
using MainframeEngine.Video;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// The video timeline's visible window and its coordinate maths, in dp. The constants are copied from Cutback's
/// TimelineControl, so the timeline feels the same as the app it came from. Pure, so the canvas, the controller and the
/// tests share one definition.
/// </summary>
public sealed class VideoTimelineView
{
    public const float RulerHeight = 22;
    public const float BoundaryGrab = 6;
    public const float DragThreshold = 4;
    public const double MinSpanSeconds = 0.25;
    public const double WheelZoomBase = 1.25;
    public const double WheelPanFraction = 0.1;
    public const double FollowLead = 0.1;
    public const double LabelSpacing = 80;

    public static readonly double[] NiceIntervals = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600];

    private float _width = 1;

    public double Duration { get; private set; }

    public double Start { get; private set; }

    public double Span { get; private set; } = 1;

    public double End => Start + Span;

    /// <summary>The canvas width in dp; never less than 1.</summary>
    public float Width
    {
        get => _width;
        set => _width = MathF.Max(1, value);
    }

    public int Version { get; private set; }

    public double SecondsPerPixel => Span / Width;

    /// <summary>Shows the whole recording. The zoom is not saved.</summary>
    public void Reset(double duration)
    {
        Duration = Math.Max(0, duration);
        SetView(0, Duration);
    }

    public void SetView(double start, double span)
    {
        var min = Math.Min(MinSpanSeconds, Duration);
        span = Math.Clamp(span, Math.Max(min, 1e-6), Math.Max(Duration, 1e-6));
        start = Math.Clamp(start, 0, Math.Max(0, Duration - span));
        if (start == Start && span == Span)
            return;
        Start = start;
        Span = span;
        Version++;
    }

    public double TimeToX(double time) => (time - Start) / Span * Width;

    public double XToTime(double x) => Start + x / Width * Span;

    public double ClampTime(double time) => Math.Clamp(time, 0, Duration);

    /// <summary>Wheel zoom about <paramref name="x"/>: the time under the cursor stays put.</summary>
    public void ZoomAbout(double x, double wheelDeltaY)
    {
        var anchor = XToTime(x);
        var span = Math.Clamp(Span * Math.Pow(WheelZoomBase, -wheelDeltaY), Math.Min(MinSpanSeconds, Duration), Duration);
        SetView(anchor - x / Width * span, span);
    }

    public void Pan(double seconds) => SetView(Start + seconds, Span);

    /// <summary>While playing, re-centres when <paramref name="time"/> leaves the view. Returns true if the view moved.</summary>
    public bool KeepVisible(double time)
    {
        if (Span >= Duration || (time >= Start && time <= End))
            return false;
        var version = Version;
        SetView(time - FollowLead * Span, Span);
        return Version != version;
    }

    /// <summary>The boundary (index ≥ 1, the start of segment i) within <see cref="BoundaryGrab"/> dp of x, below the ruler; else -1.</summary>
    public int BoundaryAt(IReadOnlyList<Segment> segments, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (y < RulerHeight)
            return -1;
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 1; i < segments.Count; i++)
        {
            var d = Math.Abs(TimeToX(segments[i].Start) - x);
            if (d < BoundaryGrab && d < bestDistance)
                (best, bestDistance) = (i, d);
        }

        return best;
    }

    /// <summary>The segment under (x, y), when the point is below the ruler and inside the canvas; else -1.</summary>
    public int SegmentAt(SegmentList segments, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (y < RulerHeight || x < 0 || x > Width)
            return -1;
        return segments.IndexAt(ClampTime(XToTime(x)));
    }

    /// <summary>The first nice interval at least <see cref="LabelSpacing"/> dp wide, and its minor tick (a fifth of it; a quarter from a minute up).</summary>
    public static (double Major, double Minor) RulerIntervals(double pixelsPerSecond)
    {
        var major = NiceIntervals[^1];
        foreach (var interval in NiceIntervals)
            if (interval * pixelsPerSecond >= LabelSpacing)
            {
                major = interval;
                break;
            }

        return (major, major >= 60 ? major / 4 : major / 5);
    }
}
```

- [ ] **Step 4: Implement `VideoCanvasState` and `VideoTimelineCanvas`**

`VideoCanvasState` is the plain class shown under **Interfaces**.

`VideoTimelineCanvas` ports Cutback's `TimelineControl.DrawOperation.Render`. Its colours are `static readonly Vector4`
fields built with `Rgb`:

| Field | RGB | Field | RGB |
|---|---|---|---|
| RulerBackground | 0x18,0x18,0x1C | BodyBackground | 0x12,0x12,0x15 |
| Kept | 0x1D,0x2E,0x4D | KeptHover | 0x26,0x40,0x6A |
| Removed | 0x1F,0x1F,0x24 | RemovedHover | 0x2A,0x2A,0x31 |
| KeptWave | 0x6F,0xA3,0xF0 | RemovedWave | 0x45,0x45,0x4E |
| Boundary | 0xC8,0xC8,0xD2 at alpha 0xB0/255 | BoundaryActive | 0xFF,0xD1,0x66 |
| SectionFill | 0xFF,0xD1,0x66 at alpha 0x38/255 | Playhead | 0xFF,0x5C,0x5C |
| Tick | 0x4E,0x4E,0x58 | Label | 0x9A,0x9A,0xA6 |
| ReasonText | 0x7C,0x7C,0x88 | Hint | 0x60,0x60,0x6A |
| Marker | 0xB4,0x8C,0xFF | | |

`BucketsForColumn`:

```csharp
public static (int First, int Last) BucketsForColumn(double viewStart, double secondsPerPixel, double secondsPerBucket, int column, int bucketCount)
{
    var first = (int)Math.Floor((viewStart + column * secondsPerPixel) / secondsPerBucket);
    if (first >= bucketCount || first < 0)
        return (-1, -1);
    var last = (int)Math.Floor((viewStart + (column + 1) * secondsPerPixel) / secondsPerBucket) - 1;
    return (first, Math.Clamp(last, first, bucketCount - 1));
}
```

`OnDraw()` works in dp and draws in this order:
1. **Backgrounds.** The ruler background over `[0, RulerHeight)` and the body background below it. If
   `Duration <= 0`, draw `Text("Open a recording to see its timeline", 12, h/2, 13, Hint)` and return.
2. **Segments.**
   - Draw one `Box` per segment, from `TimeToX(Start)` to `TimeToX(End)`, clipped to `[0, w]` and below the ruler.
   - The colour is `Kept`/`Removed`, or the hover variant when `i == state.HoverSegment`.
   - A removed segment wider than 70 dp with a `Reason` also draws `Text(reason, x0 + 6, top + 14, 11, ReasonText)`.
3. **Waveform**, skipped when `document.Waveform` is null.
   - `level = waveform.LevelFor(view.SecondsPerPixel / PixelScale)`.
   - For each device-pixel column `c`, take the min and max over
     `BucketsForColumn(view.Start, spp / PixelScale, level.SecondsPerBucket, c, level.Peaks.Count)`.
   - Draw a vertical `Line` at `x = c / PixelScale`, from `mid − max·k` to `mid − min·k`, at least `1 / PixelScale`
     each side. Here `mid = Ruler + (h − Ruler) / 2` and `k = ((h − Ruler) / 2 − 4) / 32768`.
   - The colour is `KeptWave` or `RemovedWave`, depending on the segment under the column. Advance one segment index
     monotonically; never call `IndexAt` per column.
4. **Boundaries.**
   - For `i ≥ 1`, draw a `Line` at `TimeToX(segments[i].Start)`, rounded to a device-pixel centre, below the ruler.
   - Use `Boundary` at 1 dp, or `BoundaryActive` at 2 dp when `i == HoverBoundary` or `i == DragBoundary`.
5. **Pending section.** When `state.PendingSection` is set, draw a `Box` in `SectionFill` with 1 dp `BoundaryActive`
   edges.
6. **Ruler.**
   - `(major, minor) = RulerIntervals(1 / SecondsPerPixel)`.
   - Ticks use integer indices `k`, from `floor(Start/minor)` to `ceil(End/minor)`.
   - A major tick (`k % round(major/minor) == 0`) is 10 dp tall, labelled
     `Text(VideoFormat.Ruler(t), x + 3, 11, 10, Label)`. Other ticks are 5 dp tall.
   - A 1 dp `Tick` line runs along the ruler's bottom.
7. **Markers.** For each `document.Capture?.Markers` inside the view:
   - a downward triangle 8 dp wide, from `Ruler − 9` to `Ruler − 1`, in `Marker`, drawn as one `Line` per dp row;
   - its label, `Text(label, x + 6, Ruler − 2, 10, Marker)`.
8. **Playhead,** skipped when off-screen. At `x = TimeToX(state.Playhead)`: a 1.5 dp `Playhead` line over the full
   height, and a downward head 12 dp wide and 8 dp tall at the top.

- [ ] **Step 5: Tests, gates, commit**

Run: `dotnet test Tests/MainframeEngine.Editor.Tests --filter "FullyQualifiedName~Video|FullyQualifiedName~Music"`
Expected: PASS, with 15 new tests. Then run `just build && just format-check` and commit "Add the video timeline view
and canvas, sharing the dp drawing helpers with the song tab".

---

### Task 7: `VideoTimelineController` — the timeline gestures

**Files:**
- Create: `MainframeEngine.Editor/Src/Video/VideoTimelineController.cs`.
- Test: `Tests/MainframeEngine.Editor.Tests/Video/VideoTimelineControllerTests.cs`.

**Interfaces:**
- Consumes: `VideoDocument` (`Editor`, `Segments`, `SnapTime`), `VideoTimelineView`, `VideoCanvasState`,
  `EditorModifiers` and `Silk.NET.Input.Key`.
- Produces:
  ```csharp
  public enum VideoGesture { None, Pending, Scrub, Pan, Boundary, Section }
  public sealed class VideoTimelineController
  {
      public VideoTimelineController(VideoDocument document, VideoTimelineView view, VideoCanvasState state);
      public VideoGesture Gesture { get; }
      public Action<double>? Seek { get; set; }
      public Action<int, float, float>? ShowSegmentMenu { get; set; }
      public void Hover(Vector2 p);
      public void Press(Vector2 p, EditorModifiers modifiers);
      public void Drag(Vector2 p, EditorModifiers modifiers);
      public void Release(Vector2 p);
      public void CancelGesture();
      public void RightClick(Vector2 p);
      public void Wheel(Vector2 p, Vector2 delta, EditorModifiers modifiers);
      public bool DeleteHovered();
      public bool Key(Key key, EditorModifiers modifiers);
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Numerics;
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Models;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class VideoTimelineControllerTests
{
    // 10 s over 1000 dp: 100 dp per second; the body starts at y 22
    private readonly VideoDocument _doc;
    private readonly VideoTimelineView _view = new() { Width = 1000 };
    private readonly VideoCanvasState _state = new();
    private readonly VideoTimelineController _controller;
    private readonly List<double> _seeks = [];

    public VideoTimelineControllerTests()
    {
        var project = VideoDocumentTests.Project("/r/a.mp4", duration: 10) with
        {
            Segments =
            [
                Segment.Create(0, 4, true, SegmentOrigin.Auto),
                Segment.Create(4, 6, false, SegmentOrigin.Auto),
                Segment.Create(6, 10, true, SegmentOrigin.Auto),
            ],
        };
        _doc = VideoDocument.FromProject(project, null, null, []);
        _view.Reset(10);
        _controller = new VideoTimelineController(_doc, _view, _state) { Seek = _seeks.Add };
    }

    private void Click(float x, float y)
    {
        _controller.Press(new Vector2(x, y), EditorModifiers.None);
        _controller.Release(new Vector2(x, y));
    }

    [Fact]
    public void AClickTogglesTheSegmentUnderIt()
    {
        Click(500, 50);
        Assert.True(_doc.Segments.Segments[1].Enabled);
        Assert.Equal(SegmentOrigin.Manual, _doc.Segments.Segments[1].Origin);
    }

    [Fact]
    public void ASmallWobbleIsStillAClick()
    {
        _controller.Press(new Vector2(500, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(503, 50), EditorModifiers.None);
        _controller.Release(new Vector2(503, 50));
        Assert.True(_doc.Segments.Segments[1].Enabled);
    }

    [Fact]
    public void DraggingABoundaryMovesItAsOneUndoStep()
    {
        _controller.Press(new Vector2(402, 50), EditorModifiers.None);
        Assert.Equal(VideoGesture.Boundary, _controller.Gesture);
        Assert.Equal(1, _state.DragBoundary);
        _controller.Drag(new Vector2(380, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(350, 50), EditorModifiers.None);
        _controller.Release(new Vector2(350, 50));
        Assert.Equal(3.5, _doc.Segments.Segments[1].Start, 6); // no waveform: unsnapped
        Assert.Equal(-1, _state.DragBoundary);
        Assert.True(_doc.Editor.Undo());
        Assert.Equal(4, _doc.Segments.Segments[1].Start, 6);
        Assert.False(_doc.Editor.Undo());
    }

    [Fact]
    public void DraggingInTheBodyDrawsASectionWithTheOppositeState()
    {
        _controller.Press(new Vector2(100, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(300, 50), EditorModifiers.None);
        Assert.Equal(VideoGesture.Section, _controller.Gesture);
        Assert.Equal((1.0, 3.0), _state.PendingSection);
        _controller.Release(new Vector2(300, 50));
        var cut = _doc.Segments.Segments[_doc.Segments.IndexAt(2)];
        Assert.False(cut.Enabled);
        Assert.Equal((1.0, 3.0), (Math.Round(cut.Start, 6), Math.Round(cut.End, 6)));
        Assert.Null(_state.PendingSection);
    }

    [Fact]
    public void ADragRightToLeftDrawsTheSameSection()
    {
        _controller.Press(new Vector2(300, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(100, 50), EditorModifiers.None);
        _controller.Release(new Vector2(100, 50));
        Assert.False(_doc.Segments.Segments[_doc.Segments.IndexAt(2)].Enabled);
    }

    [Fact]
    public void ShiftDragPans()
    {
        _view.SetView(2, 5); // 0.005 s per dp
        _controller.Press(new Vector2(500, 50), EditorModifiers.Shift);
        _controller.Drag(new Vector2(600, 50), EditorModifiers.Shift);
        Assert.Equal(VideoGesture.Pan, _controller.Gesture);
        Assert.Equal(1.5, _view.Start, 6);
        _controller.Release(new Vector2(600, 50));
        Assert.False(_doc.Editor.History.CanUndo);
    }

    [Fact]
    public void PressingTheRulerScrubs()
    {
        _controller.Press(new Vector2(250, 10), EditorModifiers.None);
        _controller.Drag(new Vector2(300, 10), EditorModifiers.None);
        _controller.Release(new Vector2(300, 10));
        Assert.Equal([2.5, 3.0], _seeks);
        Assert.False(_doc.Editor.History.CanUndo);
    }

    [Fact]
    public void TheWheelZoomsAboutTheCursorAndShiftWheelPans()
    {
        _controller.Wheel(new Vector2(500, 50), new Vector2(0, 1), EditorModifiers.None);
        Assert.Equal(8, _view.Span, 6);
        Assert.Equal(1, _view.Start, 6);
        _controller.Wheel(new Vector2(500, 50), new Vector2(0, 1), EditorModifiers.Shift);
        Assert.Equal(1.8, _view.Start, 6);
    }

    [Fact]
    public void HoverPrefersTheBoundaryThenTheSegment()
    {
        _controller.Hover(new Vector2(401, 50));
        Assert.Equal((1, -1), (_state.HoverBoundary, _state.HoverSegment));
        _controller.Hover(new Vector2(500, 50));
        Assert.Equal((-1, 1), (_state.HoverBoundary, _state.HoverSegment));
    }

    [Fact]
    public void DeleteDissolvesTheHoveredSegment()
    {
        _controller.Hover(new Vector2(500, 50));
        Assert.True(_controller.Key(Key.Delete, EditorModifiers.None));
        Assert.Single(_doc.Segments.Segments);
        _controller.Hover(new Vector2(500, 5));
        Assert.False(_controller.Key(Key.Backspace, EditorModifiers.None));
    }

    [Fact]
    public void RightClickOnASegmentAsksForItsMenu()
    {
        (int Index, float X, float Y)? asked = null;
        _controller.ShowSegmentMenu = (i, x, y) => asked = (i, x, y);
        _controller.RightClick(new Vector2(500, 50));
        Assert.Equal((1, 500f, 50f), asked);
        asked = null;
        _controller.RightClick(new Vector2(500, 5));
        Assert.Null(asked);
    }

    [Fact]
    public void CancelDuringABoundaryDragRecordsOneStep()
    {
        _controller.Press(new Vector2(402, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(350, 50), EditorModifiers.None);
        _controller.CancelGesture();
        Assert.Equal(VideoGesture.None, _controller.Gesture);
        Assert.False(_doc.Editor.IsDragging);
        Assert.Equal(3.5, _doc.Segments.Segments[1].Start, 6);
        Assert.True(_doc.Editor.Undo());
        Assert.Equal(4, _doc.Segments.Segments[1].Start, 6);
    }

    [Fact]
    public void CancelWhileDrawingDropsTheSection()
    {
        _controller.Press(new Vector2(100, 50), EditorModifiers.None);
        _controller.Drag(new Vector2(300, 50), EditorModifiers.None);
        _controller.CancelGesture();
        Assert.Null(_state.PendingSection);
        Assert.False(_doc.Editor.History.CanUndo);
    }
}
```

- [ ] **Step 2: Implement**

```csharp
using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Video;

public enum VideoGesture { None, Pending, Scrub, Pan, Boundary, Section }

/// <summary>
/// The video timeline's gestures, ported from Cutback's TimelineControl. Positions are in dp, local to the canvas.
/// <list type="bullet">
/// <item>Click a segment to toggle it.</item>
/// <item>Drag a boundary to move it. It snaps to the quietest sample on release, and the whole drag is one undo step.</item>
/// <item>Drag in the body to draw a section with the opposite state of where the drag started.</item>
/// <item>Shift+drag pans; pressing or dragging in the ruler scrubs.</item>
/// <item>The wheel zooms about the cursor; Shift or a horizontal wheel pans.</item>
/// <item>Right-click opens the segment's menu; Delete/Backspace dissolves the hovered segment.</item>
/// </list>
/// </summary>
public sealed class VideoTimelineController
{
    private readonly VideoDocument _doc;
    private readonly VideoTimelineView _view;
    private readonly VideoCanvasState _state;
    private Vector2 _press;
    private double _anchor;
    private double _panStart;

    public VideoTimelineController(VideoDocument document, VideoTimelineView view, VideoCanvasState state)
    {
        _doc = document ?? throw new ArgumentNullException(nameof(document));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public VideoGesture Gesture { get; private set; }

    /// <summary>Called with source seconds when the ruler is scrubbed.</summary>
    public Action<double>? Seek { get; set; }

    /// <summary>Called on a right-click over a segment, with the segment index and the local x and y.</summary>
    public Action<int, float, float>? ShowSegmentMenu { get; set; }

    public void Hover(Vector2 p)
    {
        if (Gesture != VideoGesture.None)
            return;
        var boundary = _view.BoundaryAt(_doc.Segments.Segments, p.X, p.Y);
        _state.HoverBoundary = boundary;
        _state.HoverSegment = boundary >= 0 ? -1 : _view.SegmentAt(_doc.Segments, p.X, p.Y);
    }

    public void Press(Vector2 p, EditorModifiers modifiers)
    {
        CancelGesture();
        _press = p;
        if (p.Y < VideoTimelineView.RulerHeight)
        {
            Gesture = VideoGesture.Scrub;
            Seek?.Invoke(_view.ClampTime(_view.XToTime(p.X)));
            return;
        }

        var boundary = _view.BoundaryAt(_doc.Segments.Segments, p.X, p.Y);
        if (boundary >= 0)
        {
            Gesture = VideoGesture.Boundary;
            _state.DragBoundary = boundary;
            _doc.Editor.BeginBoundaryDrag();
            return;
        }

        Gesture = VideoGesture.Pending;
    }

    public void Drag(Vector2 p, EditorModifiers modifiers)
    {
        switch (Gesture)
        {
            case VideoGesture.Scrub:
                Seek?.Invoke(_view.ClampTime(_view.XToTime(p.X)));
                break;
            case VideoGesture.Boundary:
                _doc.Editor.UpdateBoundaryDrag(_state.DragBoundary, _view.XToTime(p.X));
                break;
            case VideoGesture.Pending when Math.Abs(p.X - _press.X) > VideoTimelineView.DragThreshold:
                if ((modifiers & EditorModifiers.Shift) != 0)
                {
                    Gesture = VideoGesture.Pan;
                    _panStart = _view.Start;
                }
                else
                {
                    Gesture = VideoGesture.Section;
                    _anchor = _view.ClampTime(_view.XToTime(_press.X));
                }

                Drag(p, modifiers);
                break;
            case VideoGesture.Section:
                var t = _view.ClampTime(_view.XToTime(p.X));
                _state.PendingSection = (Math.Min(_anchor, t), Math.Max(_anchor, t));
                break;
            case VideoGesture.Pan:
                _view.SetView(_panStart - (p.X - _press.X) * _view.SecondsPerPixel, _view.Span);
                break;
        }
    }

    public void Release(Vector2 p)
    {
        switch (Gesture)
        {
            case VideoGesture.Pending:
                _doc.Editor.Toggle(_view.SegmentAt(_doc.Segments, p.X, p.Y));
                break;
            case VideoGesture.Boundary:
                _doc.Editor.EndBoundaryDrag(_state.DragBoundary, _doc.SnapTime(_view.XToTime(p.X)));
                break;
            case VideoGesture.Section when _state.PendingSection is { } section:
                var start = _view.ClampTime(_doc.SnapTime(section.Start));
                var end = _view.ClampTime(_doc.SnapTime(section.End));
                if (end > start)
                    _doc.Editor.CreateSection(start, end, _anchor);
                break;
        }

        Reset();
    }

    /// <summary>
    /// Called when the pointer is lost, or another tab or mode activates. A boundary drag ends where it is, unsnapped, as
    /// one step; a section being drawn is dropped.
    /// </summary>
    public void CancelGesture()
    {
        if (Gesture == VideoGesture.Boundary && _doc.Editor.IsDragging)
            _doc.Editor.EndBoundaryDrag(_state.DragBoundary, _doc.Segments.Segments[_state.DragBoundary].Start);
        Reset();
    }

    public void RightClick(Vector2 p)
    {
        var segment = _view.SegmentAt(_doc.Segments, p.X, p.Y);
        if (segment < 0)
            return;
        _state.HoverSegment = segment;
        ShowSegmentMenu?.Invoke(segment, p.X, p.Y);
    }

    public void Wheel(Vector2 p, Vector2 delta, EditorModifiers modifiers)
    {
        if (delta.X != 0)
            _view.Pan(delta.X * _view.Span * VideoTimelineView.WheelPanFraction);
        else if ((modifiers & EditorModifiers.Shift) != 0)
            _view.Pan(delta.Y * _view.Span * VideoTimelineView.WheelPanFraction);
        else if (delta.Y != 0)
            _view.ZoomAbout(p.X, delta.Y);
    }

    /// <summary>Dissolves the hovered segment into its neighbours (Delete, Backspace, or the menu's Delete).</summary>
    public bool DeleteHovered()
    {
        if (!_doc.Editor.Dissolve(_state.HoverSegment))
            return false;
        _state.HoverSegment = -1;
        return true;
    }

    public bool Key(Key key, EditorModifiers modifiers) =>
        key is Silk.NET.Input.Key.Delete or Silk.NET.Input.Key.Backspace && modifiers == EditorModifiers.None && DeleteHovered();

    private void Reset()
    {
        Gesture = VideoGesture.None;
        _state.DragBoundary = -1;
        _state.PendingSection = null;
    }
}
```

- [ ] **Step 3: Tests, gates, commit.** The tests pass (13). Commit "Add the video timeline gestures".

---

### Task 8: `VideoPlaybackPlan` — what plays when (pure)

**Files:**
- Create: `MainframeEngine.Editor/Src/Video/VideoPlaybackPlan.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Video/VideoPlaybackPlanTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public readonly record struct PlayRange(double Start, double End) { public double Length => End - Start; }
  public sealed class VideoPlaybackPlan
  {
      public const double PrefetchSeconds = 1.0;
      public static VideoPlaybackPlan Build(IReadOnlyList<Segment> segments, double from);
      public IReadOnlyList<PlayRange> Ranges { get; }
      public double OutputDuration { get; }
      public double OutputStartOf(int range);
      public int RangeAt(double output);              // -1 once finished
      public double SourceTimeAt(double output);      // the last range's end once finished; 0 for an empty plan
      public bool IsFinished(double output);
      public bool ShouldPrefetch(int range, double output);
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class VideoPlaybackPlanTests
{
    // kept [0,4) · cut [4,6) · kept [6,10)
    private static readonly Segment[] Segments =
    [
        Segment.Create(0, 4, true, SegmentOrigin.Auto),
        Segment.Create(4, 6, false, SegmentOrigin.Auto),
        Segment.Create(6, 10, true, SegmentOrigin.Auto),
    ];

    [Fact]
    public void FromTheStartEveryKeptSegmentPlaysInOrder()
    {
        var plan = VideoPlaybackPlan.Build(Segments, 0);
        Assert.Equal([new PlayRange(0, 4), new PlayRange(6, 10)], plan.Ranges);
        Assert.Equal(8, plan.OutputDuration, 9);
        Assert.Equal(4, plan.OutputStartOf(1), 9);
    }

    [Fact]
    public void FromInsideAKeptSegmentTheFirstRangeStartsAtThePlayhead() =>
        Assert.Equal([new PlayRange(2, 4), new PlayRange(6, 10)], VideoPlaybackPlan.Build(Segments, 2).Ranges);

    [Fact]
    public void FromInsideACutPlaybackStartsAtTheNextKeptSegment() =>
        Assert.Equal([new PlayRange(6, 10)], VideoPlaybackPlan.Build(Segments, 5).Ranges);

    [Fact]
    public void FromTheEndThereIsNothingToPlay()
    {
        var plan = VideoPlaybackPlan.Build(Segments, 10);
        Assert.Empty(plan.Ranges);
        Assert.True(plan.IsFinished(0));
        Assert.Equal(-1, plan.RangeAt(0));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(3.0, 3.0)]
    [InlineData(4.0, 6.0)]
    [InlineData(5.0, 7.0)]
    [InlineData(8.0, 10.0)]
    [InlineData(9.0, 10.0)]
    public void OutputTimeMapsOverTheCut(double output, double source) =>
        Assert.Equal(source, VideoPlaybackPlan.Build(Segments, 0).SourceTimeAt(output), 9);

    [Fact]
    public void TheBoundaryBelongsToTheNextRange()
    {
        var plan = VideoPlaybackPlan.Build(Segments, 0);
        Assert.Equal(0, plan.RangeAt(3.999));
        Assert.Equal(1, plan.RangeAt(4));
        Assert.True(plan.IsFinished(8));
    }

    [Fact]
    public void TheNextDecoderStartsOneSecondEarly()
    {
        var plan = VideoPlaybackPlan.Build(Segments, 0);
        Assert.False(plan.ShouldPrefetch(0, 2.9));
        Assert.True(plan.ShouldPrefetch(0, 3.0));
        Assert.False(plan.ShouldPrefetch(1, 7.5)); // the last range has nothing after it
    }

    [Fact]
    public void SliversShorterThanAMillisecondAreSkipped()
    {
        Segment[] segments = [Segment.Create(0, 4, true, SegmentOrigin.Auto), Segment.Create(4, 10, false, SegmentOrigin.Auto)];
        Assert.Empty(VideoPlaybackPlan.Build(segments, 3.9995).Ranges);
    }
}
```

- [ ] **Step 2: Implement**

```csharp
using MainframeEngine.Video;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Video;

/// <summary>A kept stretch of the source, in source seconds.</summary>
public readonly record struct PlayRange(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>
/// What the preview plays from a given playhead: the kept segments from there on, laid end to end on an output timeline.
/// The audio voice counts output seconds, and this plan maps them back to the source frame to show. The next range's
/// video decoder starts <see cref="PrefetchSeconds"/> before the current range ends, so the switch has no gap.
/// </summary>
public sealed class VideoPlaybackPlan
{
    public const double PrefetchSeconds = 1.0;

    private readonly PlayRange[] _ranges;
    private readonly double[] _outputStarts;

    private VideoPlaybackPlan(PlayRange[] ranges)
    {
        _ranges = ranges;
        _outputStarts = new double[ranges.Length];
        var t = 0.0;
        for (var i = 0; i < ranges.Length; i++)
        {
            _outputStarts[i] = t;
            t += ranges[i].Length;
        }

        OutputDuration = t;
    }

    public IReadOnlyList<PlayRange> Ranges => _ranges;

    public double OutputDuration { get; }

    public static VideoPlaybackPlan Build(IReadOnlyList<Segment> segments, double from)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var ranges = new List<PlayRange>();
        foreach (var segment in segments)
        {
            if (!segment.Enabled || segment.End <= from)
                continue;
            var start = Math.Max(segment.Start, from);
            if (segment.End - start >= SegmentList.MinSegmentLength)
                ranges.Add(new PlayRange(start, segment.End));
        }

        return new VideoPlaybackPlan([.. ranges]);
    }

    public double OutputStartOf(int range) => _outputStarts[range];

    public bool IsFinished(double output) => output >= OutputDuration;

    public int RangeAt(double output)
    {
        if (IsFinished(output))
            return -1;
        for (var i = _ranges.Length - 1; i >= 0; i--)
            if (output >= _outputStarts[i])
                return i;
        return 0;
    }

    public double SourceTimeAt(double output)
    {
        if (_ranges.Length == 0)
            return 0;
        output = Math.Max(0, output);
        var i = RangeAt(output);
        return i < 0 ? _ranges[^1].End : _ranges[i].Start + (output - _outputStarts[i]);
    }

    public bool ShouldPrefetch(int range, double output) =>
        range >= 0 && range + 1 < _ranges.Length && output >= _outputStarts[range] + _ranges[range].Length - PrefetchSeconds;
}
```

- [ ] **Step 3: Tests, gates, commit.** The tests pass (13). Commit "Add VideoPlaybackPlan: the kept ranges from the
  playhead on an output timeline".

---
### Task 9: `VideoPreview` — decoders, the texture, the audio voice and the clock

**Files:**
- Create: `MainframeEngine.Editor/Src/Video/VideoDecoders.cs` and `MainframeEngine.Editor/Src/Video/VideoPreview.cs`
- Test: `Tests/MainframeEngine.Editor.Tests/Video/VideoPreviewTests.cs`

**Interfaces:**
- Consumes:
  - `PreviewDecoding` and `FfmpegProcess` (Task 2), and `VideoPlaybackPlan` (Task 8).
  - Audio: `AudioServer` (`SampleRate`, `PlayOneShot`, `GetGeneratorPlayback`, `Stop`, `IsPlaying`),
    `AudioStreamGenerator`, `AudioStreamGeneratorPlayback` (`PushFrames`, `FramesAvailable`, `FramesConsumed`,
    `ClearBuffer`), `AudioBusLayout.MasterBus`, `ProcessMode.Always`.
  - Rendering: `SubViewport`, `Sprite2D`, `Texture2D.FromPixels`/`SetPixels`.
- Produces:
  ```csharp
  public interface IVideoDecoder : IDisposable { Stream Output { get; } bool Failed { get; } }
  public interface IVideoDecoderFactory { IVideoDecoder Start(IReadOnlyList<string> arguments); }
  public sealed class FfmpegVideoDecoderFactory(string ffmpegPath) : IVideoDecoderFactory;   // a missing binary → a decoder whose Failed is true
  public sealed class VideoPreview : IDisposable
  {
      public VideoPreview(IVideoDecoderFactory decoders, Func<AudioServer?> audio, string sourcePath, int sourceWidth, int sourceHeight, double sourceFrameRate, int number);
      public SubViewport Viewport { get; }
      public int Width { get; }
      public int Height { get; }
      public bool IsPlaying { get; }
      public double Position { get; }                     // source seconds
      public void Play(IReadOnlyList<Segment> segments, double from, int segmentsVersion = 0);
      public void Pause();
      public void Seek(double time);                      // paused: decodes that one frame (newest request wins)
      public void Update(IReadOnlyList<Segment> segments, int segmentsVersion);   // main thread, every frame
      internal int LiveDecoders { get; }
      public void Dispose();
  }
  ```

**Design (follow it exactly):**

*Size and texture.*
- `(Width, Height) = PreviewDecoding.PreviewSize(sourceWidth, sourceHeight)`, decoded at
  `fps = PreviewDecoding.PreviewFrameRate(sourceFrameRate)`.
- The texture is `Texture2D.FromPixels(Width, Height, black)`, created once from `new byte[Width * Height * 4]` with
  every alpha byte set to 255.
- Its only parent is `Viewport = new SubViewport { Name = $"VideoPreview{number}", Disable3D = true, UpdateMode = SubViewportUpdateMode.Disabled, Width = Width, Height = Height, ClearColor = Color.FromArgb(255, 0, 0, 0) }`,
  holding a `new Sprite2D { Texture = _texture, Centered = false }`.

*Video.*
- Each started range gets a `VideoStream`: an `IVideoDecoder` on
  `PreviewDecoding.VideoArguments(source, r.Start, r.End, Width, Height, fps)`, plus a reader task created with
  `TaskCreationOptions.LongRunning`.
- The reader reads exactly `Width * Height * 4` bytes per frame into buffers from a pool of 6 preallocated `byte[]`.
  A `SemaphoreSlim(6)` makes a full pool block the reader, which back-pressures ffmpeg.
- Each frame goes on a `ConcurrentQueue<(double OutputTime, byte[] Frame)>`, where
  `OutputTime = plan.OutputStartOf(range) + n / fps`.
- `Update` takes the latest queued frame whose `OutputTime ≤ clock`, returns any older buffers to the pool, and calls
  `_texture.SetPixels(frame)` at most once per call.
- The stream for `r + 1` starts when `plan.ShouldPrefetch(r, clock)`. A stream is disposed (its process killed) once
  the clock passes its range's end.

*Audio.* This exists only when an `AudioServer` does.
- One task decodes range after range with `PreviewDecoding.AudioArguments(source, r.Start, r.End, server.SampleRate)`.
- It pushes into a single `AudioStreamGenerator { MixRate = server.SampleRate, BufferSeconds = 1.0f }` voice, started
  with `server.PlayOneShot(stream, AudioBusLayout.MasterBus, priority: int.MaxValue, processMode: ProcessMode.Always)`
  and opened with `GetGeneratorPlayback(handle)`. This follows `Music/Engine/SongPlayer.cs:195-234`.
- It reads 4096-frame chunks (`4096 * 8` bytes), keeps any remainder so only whole stereo frames are pushed, and calls
  `PushFrames(MemoryMarshal.Cast<byte, float>(…))` once `FramesAvailable ≥ chunk`, sleeping 2 ms otherwise.

*Clock.*
- With audio, `output = (playback.FramesConsumed − consumedAtStart) / sampleRate`.
- Without an `AudioServer` (tests, or no device), a `Stopwatch` started in `Play` is the clock.
- `Position = plan.SourceTimeAt(output)`. When `plan.IsFinished(output)`, call `Pause()` and leave `Position` at the
  last range's end.

*Edits while playing.* `Update(segments, version)` with a version that differs from `Play`'s calls
`Play(segments, Position, version)`, which restarts from the playhead.

*Pause, Dispose and Play.*
- Each cancels the audio task and every reader, and disposes every decoder (`IVideoDecoder.Dispose` kills the process
  tree).
- Each stops the voice with `server.Stop(handle)` and calls `ClearBuffer()`.
- `Dispose` also frees the viewport if it is not `IsFreed`.

*Seek while paused.* Start one decoder on `PreviewDecoding.FrameArguments`. If one is already running, keep only the
newest target and start it once the current decode finishes.

*Failures.* A decoder whose `Failed` becomes true logs `Log.Warning($"[Video] Preview decode failed: {…}")` once and
pauses. A preview problem never opens a dialog.

*`FfmpegVideoDecoder`.*
- It wraps `FfmpegProcess.Start(ffmpegPath, args, redirectStandardOutput: true)` and drains stderr into a string with
  `ReadToEndAsync`.
- `Output` is `StandardOutput.BaseStream`.
- `Failed` is true when the process has exited with a non-zero code, or when starting it threw `Win32Exception` (no
  such binary).
- `Dispose` calls `FfmpegProcess.TryKill`, then `Dispose`.

- [ ] **Step 1: Write the failing tests** (a fake factory, no audio server)

```csharp
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Tests.Video;

[Collection(nameof(SerialEditor))]
public sealed class VideoPreviewTests
{
    private sealed class FakeDecoder(byte[] bytes) : IVideoDecoder
    {
        public Stream Output { get; } = new MemoryStream(bytes);
        public bool Failed => false;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeFactory(int frameBytes) : IVideoDecoderFactory
    {
        public List<(IReadOnlyList<string> Args, FakeDecoder Decoder)> Started { get; } = [];

        public IVideoDecoder Start(IReadOnlyList<string> arguments)
        {
            var decoder = new FakeDecoder(new byte[frameBytes * 90]); // 3 s of frames at 30 fps
            Started.Add((arguments, decoder));
            return decoder;
        }
    }

    private static readonly Segment[] Segments =
    [
        Segment.Create(0, 4, true, SegmentOrigin.Auto),
        Segment.Create(4, 6, false, SegmentOrigin.Auto),
        Segment.Create(6, 10, true, SegmentOrigin.Auto),
    ];

    private static VideoPreview Preview(FakeFactory factory) => new(factory, () => null, "/r/a.mp4", 640, 360, 30, number: 1);

    [Fact]
    public void ThePreviewIsAtMost960WideWithTheSourceAspect()
    {
        using var preview = new VideoPreview(new FakeFactory(4), () => null, "/r/a.mp4", 1920, 1080, 60, 1);
        Assert.Equal((960, 540), (preview.Width, preview.Height));
        Assert.Equal(960, preview.Viewport.Width);
    }

    [Fact]
    public void PlayStartsTheFirstRangesVideoDecoder()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Play(Segments, 2);
        Assert.True(preview.IsPlaying);
        Assert.Equal(["-ss", "2", "-i", "/r/a.mp4", "-t", "2"], Assert.Single(factory.Started).Args.Take(6));
    }

    [Fact]
    public void PauseKillsEveryDecoder()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Play(Segments, 0);
        preview.Pause();
        Assert.False(preview.IsPlaying);
        Assert.All(factory.Started, s => Assert.True(s.Decoder.Disposed));
        Assert.Equal(0, preview.LiveDecoders);
    }

    [Fact]
    public void DisposeKillsEveryDecoder()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        var preview = Preview(factory);
        preview.Play(Segments, 0);
        preview.Dispose();
        Assert.All(factory.Started, s => Assert.True(s.Decoder.Disposed));
    }

    [Fact]
    public void ASegmentEditRebuildsThePlanFromThePlayhead()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Play(Segments, 0, segmentsVersion: 0);
        Segment[] edited = [Segment.Create(0, 1, true, SegmentOrigin.Auto), Segment.Create(1, 10, false, SegmentOrigin.Manual)];
        preview.Update(edited, segmentsVersion: 1);
        Assert.Equal(2, factory.Started.Count);
        Assert.True(factory.Started[0].Decoder.Disposed);
    }

    [Fact]
    public void SeekWhilePausedDecodesOneFrame()
    {
        var factory = new FakeFactory(640 * 360 * 4);
        using var preview = Preview(factory);
        preview.Seek(3.5);
        Assert.Contains("-frames:v", Assert.Single(factory.Started).Args);
        Assert.Equal(3.5, preview.Position, 9);
    }
}
```

If `Texture2D.FromPixels`, `Sprite2D` or `SubViewport` cannot be created outside a `SceneTree`, run these tests on
`SessionHost` (`SessionFixture.cs`) with `Host.AddChild(preview.Viewport)`. The song tab builds its viewports before
they enter the tree, which suggests this will not be needed.

- [ ] **Step 2: Implement** `VideoDecoders.cs` and `VideoPreview.cs` as designed. Reader threads never touch the
  texture.

- [ ] **Step 3: Tests (6 pass), gates, commit.** Commit message: "Add VideoPreview: gapless ffmpeg preview into a texture, with the audio voice as its clock".

---

### Task 10: `VideoTab` in the session — open, save, close, undo, File › Open, recent videos

**Files:**
- Create: `MainframeEngine.Editor/Src/Video/VideoTab.cs`, `VideoJob.cs`, `VideoFfmpeg.cs`.
- Modify:
  - `MainframeEngine.Editor/Src/Session/EditorSession.cs`: `OpenVideo` and `SaveVideo`; branches in `Close`,
    `Activate`, `Deactivate`, `Tick` and `Dispose`; `ActiveVideo`.
  - `MainframeEngine.Editor/Src/EditorCommands.cs`: `RunVideo`; `OpenVideo`, `SaveVideo` and `TrySaveVideo`;
    `OpenAny`; `file.open` per mode; `video.recent:{i}`; `CloseTab`, with `CloseSong` generalised into
    `CloseWithPrompt`; both `SaveAll`s; the File menu's recent list. Also add these to `IsRecoverable`:
    `MediaProbeException`, `ProjectFormatException`, `InvalidPartitionException`, `FfmpegException`,
    `FfmpegMissingException` and `CaptureSidecarFormatException`.
  - `MainframeEngine.Editor/Src/Settings/EditorSettings.cs`: `VideoFfmpegPath` and `RecentVideos` (load, save, clone).
  - `MainframeEngine.Editor/Src/FileSystem/FileKind.cs`: `Video` (`.mp4 .mov .mkv .webm`) and `VideoProject`
    (`.mfcut .cutback`).
  - `MainframeEngine.Editor/Src/UI/EditorIcons.cs`: `.mfcut` and `.cutback` → `video`; the video files → `movie`.
  - `MainframeEngine.Editor/Src/UI/FileSystemPanel.cs`: the `Open` cases and the unsaved badge (around line 221).
  - `MainframeEngine.Editor/Src/Qa/EditorQaScript.cs`: the `open` verb, and a new `mode game|video` verb.
  - `MainframeEngine.Editor/Src/EditorWorkspace.cs`: `OnUnhandledInput`, and wiring the dirty events into
    `ScenesChanged`.
- Test: `Tests/MainframeEngine.Editor.Tests/Video/VideoTabTests.cs`.

**Interfaces:**
- Consumes: Tasks 4–9.
- Produces:
  ```csharp
  public sealed class VideoJob<T> : IDisposable
  {
      public static VideoJob<T> Start(string name, Func<IProgress<double>, CancellationToken, Task<T>> work);
      public string Name { get; }
      public Task<T> Task { get; }
      public double Progress { get; }
      public bool IsDone { get; }
      public bool IsCancelled { get; }
      public void Cancel();
      public void Dispose();
  }
  public static class VideoFfmpeg
  {
      public const string EnvironmentVariable = "MAINFRAME_FFMPEG";
      public static string? ConfiguredPath(string? setting, string? environment);
      public static FfmpegLocation Locate(string? setting);   // throws FfmpegMissingException
  }
  public sealed class VideoTab : IEditorTab
  {
      internal VideoTab(VideoDocument document, VideoPreview preview, FfmpegLocation? ffmpeg, int number);
      public EditorMode Mode => EditorMode.Video;
      public VideoDocument Document { get; }
      public VideoPreview Preview { get; }
      public FfmpegLocation? Ffmpeg { get; }
      public VideoTimelineView View { get; }
      public VideoCanvasState State { get; }
      public VideoTimelineController Controller { get; }
      public VideoTimelineCanvas Timeline { get; }
      public SubViewport TimelineViewport { get; }
      public string? Notice { get; set; }
      public VideoJob<Waveform>? WaveformJob { get; }
      public VideoJob<IReadOnlyList<SilenceSpan>>? SilenceJob { get; set; }
      public VideoJob<IReadOnlyList<Word>>? TranscribeJob { get; set; }
      public VideoJob<string>? ExportJob { get; set; }
      public int CurrentWord { get; }                 // TranscriptView.IndexAtTime at the playhead
      public void TogglePlay();
      public void Stop();
      public void Seek(double time);
      internal void Tick();
      internal void Deactivate();
      internal event Action<VideoTab>? DirtyChanged;
  }
  // EditorSession
  public VideoTab? ActiveVideo => ActiveTab as VideoTab;
  public VideoTab OpenVideo(string path, FfmpegLocation ffmpeg, int historyLimit);
  public void SaveVideo(VideoTab tab, string? path = null);
  // EditorCommands
  public void OpenVideo(string path);
  public void OpenAny(string path);
  public void SaveVideo(VideoTab tab, bool saveAs, Action<bool>? then);
  // EditorSettings
  public string? VideoFfmpegPath { get; set; }
  public List<string> RecentVideos { get; set; }   // most recent first, at most 10, full paths
  ```

**Behaviour:**

*`VideoJob<T>`* copies `SongRenderJob`'s pattern: `Task.Run`, a private `IProgress<double>` sink that `Volatile.Write`s
the field `Progress` reads, a `CancellationTokenSource`, `Cancel` and `Dispose`.

*`VideoTab`.*
- The constructor builds:
  - `View`, with `Reset(document.Duration)`;
  - `State`, and `Controller` with `Seek = Seek`;
  - `Timeline`, as the only child of `TimelineViewport = new SubViewport { Name = $"VideoTimeline{number}", Disable3D = true, UpdateMode = Disabled, ClearColor = Color.FromArgb(255, 0x12, 0x12, 0x15), Width = 16, Height = 16 }`;
  - `Notice = string.Join(" ", document.Warnings)`, or null when there are none;
  - when `ffmpeg` is not null, `WaveformJob = VideoJob<Waveform>.Start("waveform", (p, ct) => new WaveformExtractor(ffmpeg).ExtractAsync(document.SourcePath, document.Duration, p, ct))`.
- Names:
  - `DisplayName` is `Path.GetFileName(Document.FilePath ?? Document.DefaultProjectPath)`, and `Title` adds `*` when
    dirty.
  - `IconClasses` is `"icon icon-video icon-audio"`.
  - `Tooltip` is `{DisplayName} — video ({unsaved changes|saved}) — {FilePath ?? SourcePath}`.
- `Tick()`:
  1. When the waveform job is done, set `Document.Waveform` on success, or the notice
     `"Waveform unavailable: {message}"` on failure. Then dispose the job and null it.
  2. Call `Preview.Update(Document.Segments.Segments, Document.Version)`.
  3. Set `State.Playhead = Preview.Position`, and update `CurrentWord`.
  4. While playing, call `View.KeepVisible(Preview.Position)`.
- `TogglePlay()` pauses when playing. Otherwise it plays from `State.Playhead`, first rewinding to 0 if the playhead is
  within 0.01 s of the end.
- `Stop()` pauses and seeks to 0.
- `Seek(t)` sets the playhead. While playing it calls `Preview.Play(…, t, Document.Version)`; otherwise
  `Preview.Seek(t)`.
- `Deactivate()` pauses, calls `Controller.CancelGesture()`, and sets both viewports to `Disabled`.
- `Dispose()` cancels and disposes every job (an export that is cancelled deletes its partial file), disposes the
  preview, and frees `TimelineViewport`.

*`EditorSession.OpenVideo(path, ffmpeg, historyLimit)`.*
- If an open `VideoTab` matches, activate it. A match is `PathsEqual` on `Document.FilePath` or `Document.SourcePath`,
  or the `.mfcut` beside a legacy file.
- Otherwise:
  1. Load the document: `var document = VideoDocument.OpenAsync(full, ffmpeg, historyLimit, CancellationToken.None).GetAwaiter().GetResult();`.
     This is safe because the main thread has no synchronisation context.
  2. Create the preview: `new VideoPreview(new FfmpegVideoDecoderFactory(ffmpeg.FfmpegPath), () => _host.Tree?.Servers.Get<AudioServer>(), …, ++_videoCounter)`.
  3. Create `new VideoTab(...)`, add both viewports with `_host.AddChild`, and hook `DirtyChanged` to `ScenesChanged`.
  4. Call `Add(tab)` (Task 4). It activates the tab, which switches the mode.
- Never call `UseProjectOf`: recordings live in user data, outside the project.

*Other session changes.*
- `SaveVideo(tab, path)` calls `tab.Document.Save(path)`, then raises `ScenesChanged`.
- The `Close` branch unhooks `DirtyChanged`.
- `Activate` and `Deactivate` call `video.Deactivate()` on the tab losing focus.
- `Tick` calls `ActiveVideo?.Tick()`.

*`EditorCommands`.*
- `Run`: add `if (Session.ActiveTab is VideoTab video && RunVideo(video, id)) return true;` before the switch.
- `RunVideo` handles:
  - `file.save` and `file.save_as`;
  - `edit.undo` and `edit.redo`, through `video.Document.Editor.Undo()` / `Redo()`;
  - `edit.history`, which returns true;
  - `edit.delete`, through `video.Controller.DeleteHovered()`;
  - `video.play` (`TogglePlay`) and `video.stop` (`Stop`);
  - anything else returns false.
- `file.open`:
  - In video mode, the picker is titled "Open Recording", with filters `["*.mp4", "*.mov", "*.mkv", "*.webm", "*.mfcut", "*.cutback"]`.
  - In game mode, `OpenScene`'s picker gains the same six filters and is renamed "Open".
  - Both pickers call `OpenAny(path)`.
- `OpenAny`:
  - a video, `.mfcut` or `.cutback` → `OpenVideo`;
  - `.msong` → `OpenSong`;
  - `.rml` → `Session.OpenUiPreview`, as the FileSystem panel does;
  - anything else → the existing scene open.
- `OpenVideo(path)`:
  ```csharp
  try
  {
      var ffmpeg = VideoFfmpeg.Locate(_workspace.Settings.VideoFfmpegPath);
      _workspace.RunWithSplash($"Opening {Path.GetFileName(path)}…", () =>
      {
          try { Session.OpenVideo(path, ffmpeg, VideoDocument.DefaultHistoryLimit); TouchRecentVideo(path); }
          catch (Exception e) when (IsRecoverable(e)) { ReportError($"Could not open {Path.GetFileName(path)}", e); }
      });
  }
  catch (FfmpegMissingException e) { ReportError("The video editor needs ffmpeg", e); }
  ```
  `TouchRecentVideo` moves the path to the front of `Settings.RecentVideos`, caps the list at 10, and saves the
  settings through `ApplySettings(clone)`.
- `video.recent:{i}` opens `RecentVideos[i]`. A path that no longer exists is removed, with the message
  `"{name} was moved or deleted."`. In video mode the File menu lists up to 5 of these under the "Recent Videos"
  header, each with the `video` icon.
- `SaveVideo(tab, saveAs, then)`:
  - When it is not a Save As and the document has a `FilePath`, call `TrySaveVideo`.
  - Otherwise, show `FilePickerModel(Save, dir of FilePath ?? DefaultProjectPath, ["*.mfcut"], file name)`, titled
    "Save Video Project".
  - `TrySaveVideo` mirrors `TrySaveSong`, and also touches the recent list.
- `CloseTab`: `SongTab { IsDirty: true } or VideoTab { IsDirty: true }` goes to `CloseWithPrompt(tab)`. That is
  `CloseSong` generalised: its button 0 saves through `SaveSong` or `SaveVideo`, by type, then closes.
- `SaveAll` (both overloads): add `VideoTab { IsDirty: true }`. An unnamed one goes through Save As, as untitled scenes
  do.

*FileSystem panel.*
- `FileKind.Video` and `FileKind.VideoProject` call `Workspace.Commands.OpenVideo(entry.FullPath)`.
- The unsaved-badge pattern gains `or Video.VideoTab`.

*QA script.*
- `open` sends `.mp4`, `.mov`, `.mkv`, `.webm`, `.mfcut` and `.cutback` to `workspace.Commands.OpenVideo(path)`.
- The new `mode game|video` verb calls `workspace.SetMode(...)`.
- Update the grammar comment at lines 8-46.

- [ ] **Step 1: Write the failing tests**

`VideoTabTests.cs` runs on `SessionHost` and `HeadlessEditor`. Each `.mfcut` source is a one-byte file, so nothing is
probed:

```csharp
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Projects;

namespace MainframeEngine.Editor.Tests.Video;

[Collection(nameof(SerialEditor))]
public sealed class VideoTabTests : IDisposable
{
    private static readonly FfmpegLocation NoFfmpeg = new("/x/ffmpeg", "/x/ffprobe", "/x", FfmpegLocationSource.Path);
    private readonly HeadlessEditor _editor = new();

    private EditorWorkspace W => _editor.Workspace;

    public void Dispose() => _editor.Dispose();

    private string VideoProject(string name = "trim")
    {
        var dir = Directory.CreateTempSubdirectory("video-tab-").FullName; // recordings live outside any project
        var video = Path.Combine(dir, name + ".mp4");
        File.WriteAllBytes(video, [0]);
        var path = Path.Combine(dir, name + ".mfcut");
        ProjectSerializer.SaveAsync(VideoDocumentTests.Project(video), path, CancellationToken.None).GetAwaiter().GetResult();
        return path;
    }

    [Fact]
    public void OpeningAProjectAddsAnActiveVideoTabAndSwitchesMode()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        Assert.Same(tab, W.Session.ActiveTab);
        Assert.Same(tab, W.Session.ActiveVideo);
        Assert.Equal(EditorMode.Video, W.Mode);
        Assert.Equal("trim.mfcut", tab.Title);
    }

    [Fact]
    public void OpeningTheSameProjectTwiceActivatesTheFirstTab()
    {
        var path = VideoProject();
        var first = W.Session.OpenVideo(path, NoFfmpeg, 100);
        W.SetMode(EditorMode.Game);
        Assert.Same(first, W.Session.OpenVideo(path, NoFfmpeg, 100));
        Assert.Single(W.Session.Tabs.OfType<VideoTab>());
    }

    [Fact]
    public void ARecordingOutsideTheProjectDoesNotChangeTheProjectRoot()
    {
        var root = W.Session.ProjectRoot;
        W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        Assert.Equal(root, W.Session.ProjectRoot);
    }

    [Fact]
    public void EditsShowAStarAndSavingClearsIt()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        tab.Document.Editor.CreateSection(10, 20, anchor: 10);
        Assert.Equal("trim.mfcut*", tab.Title);
        W.Session.SaveVideo(tab);
        Assert.Equal("trim.mfcut", tab.Title);
    }

    [Fact]
    public void UndoAndRedoCommandsReachTheVideoTab()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        tab.Document.Editor.CreateSection(10, 20, anchor: 10);
        W.Commands.Execute("edit.undo");
        Assert.Single(tab.Document.Segments.Segments);
        W.Commands.Execute("edit.redo");
        Assert.Equal(3, tab.Document.Segments.Count);
    }

    [Fact]
    public void LeavingVideoModePausesPlayback()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        tab.TogglePlay(); // its decoder fails without ffmpeg, which only logs
        W.SetMode(EditorMode.Game);
        Assert.False(tab.Preview.IsPlaying);
    }

    [Fact]
    public void ClosingTheTabFreesItsViewports()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        W.Session.Close(tab);
        Assert.Empty(W.Session.Tabs.OfType<VideoTab>());
        Assert.True(tab.TimelineViewport.IsFreed);
    }

    [Fact]
    public void ClosingADirtyVideoTabAsksToSave()
    {
        var tab = W.Session.OpenVideo(VideoProject(), NoFfmpeg, 100);
        tab.Document.Editor.CreateSection(10, 20, anchor: 10);
        W.Commands.CloseTab(tab);
        Assert.True(W.Message.Visible);
        Assert.Equal(["Save", "Don't Save", "Cancel"], W.Message.Current!.Buttons);
    }

    [Fact]
    public void RecentVideosAreMostRecentFirstAndCapped()
    {
        var s = new EditorSettings();
        for (var i = 0; i < 12; i++)
            EditorCommands.TouchRecent(s.RecentVideos, $"/r/{i}.mp4");
        Assert.Equal(10, s.RecentVideos.Count);
        Assert.Equal("/r/11.mp4", s.RecentVideos[0]);
        EditorCommands.TouchRecent(s.RecentVideos, "/r/5.mp4");
        Assert.Equal("/r/5.mp4", s.RecentVideos[0]);
        Assert.Equal(10, s.RecentVideos.Count);
    }

    [Theory]
    [InlineData("/opt/ff", "/env/ff", "/opt/ff")]
    [InlineData("  ", "/env/ff", "/env/ff")]
    [InlineData(null, "", null)]
    public void TheFfmpegSettingWinsOverTheEnvironment(string? setting, string? env, string? expected) =>
        Assert.Equal(expected, VideoFfmpeg.ConfiguredPath(setting, env));

    [Theory]
    [InlineData("a.mp4", FileKind.Video)]
    [InlineData("a.MOV", FileKind.Video)]
    [InlineData("a.webm", FileKind.Video)]
    [InlineData("a.mfcut", FileKind.VideoProject)]
    [InlineData("a.cutback", FileKind.VideoProject)]
    public void RecordingsAndVideoProjectsHaveTheirOwnFileKinds(string path, FileKind kind) =>
        Assert.Equal(kind, FileKinds.Of(path, isDirectory: false));
}
```

`EditorCommands.TouchRecent(List<string> list, string path)` is an `internal static` helper. It removes any existing
copy of the path (ignoring case on Windows), inserts the path at the front, and caps the list at 10.

- [ ] **Step 2: Implement** the behaviour above.

- [ ] **Step 3: Run the whole editor suite.** Expected: PASS. Fix any test that asserted the old File › Open filter or
  label.

- [ ] **Step 4: Gates and commit.** Commit message: "Open, save and close video tabs; File › Open takes recordings and switches to video mode".

---

### Task 11: The video-mode screen — `VideoPanel`, `VideoView`, the video toolbar, detection

**Files:**
- Modify: `MainframeEngine.Editor/Assets/Editor/video.rml` (the full layout) and
  `MainframeEngine.Editor/Src/Video/VideoPanel.cs`.
- Create: `MainframeEngine.Editor/Src/Video/VideoView.cs`.
- Modify:
  - `MainframeEngine.Editor/Assets/Editor/toolbar.rml`: the contents of `#video-tools`.
  - `MainframeEngine.Editor/Src/UI/Panels.cs`: `ToolbarPanel.UpdateVideo`.
  - `MainframeEngine.Editor/Src/EditorWorkspace.cs`: a `VideoView` property, added as a workspace child beside
    `SongView`; in video mode, `VideoView.HandleKey` runs first in `OnUnhandledInput`.
  - `MainframeEngine.Editor/Src/EditorCommands.cs`: `video.detect_idle`, `video.detect_silence`,
    `video.toggle_transcript` and `view.output`.
  - `MainframeEngine.Editor/Src/Layout/EditorLayout.cs`: `SetVideoTranscriptWidth`, `SetVideoTimelineHeight`,
    `SetVideoShowTranscript` and `SetVideoShowOutput`, all persisted.
- Test: `Tests/MainframeEngine.Editor.Tests/Video/VideoWorkspaceTests.cs`.

**Interfaces:**
- Consumes: `VideoTab` (Task 10); `SilenceDetector(ffmpeg).DetectAsync(path, duration, settings, progress, ct)`;
  `VideoDocument.ApplySilence` and `DetectIdleGameplay`.
- Produces:
  ```csharp
  [Tool] public sealed class VideoView : Node
  {
      public const string TimelineTexture = "video-timeline", PreviewTexture = "video-preview";
      public VideoView(EditorWorkspace workspace);
      public VideoTab? Tab { get; }
      public bool HandleKey(Key key, EditorModifiers modifiers);
      public void DetectSilence(VideoTab tab);          // a second call while it runs cancels
      internal void PollJobs(VideoTab tab);
  }
  public sealed class VideoPanel : EditorDocument
  {
      public LayoutRect PreviewRect { get; }
      public LayoutRect TimelineRect { get; }
      public LayoutRect TranscriptRect { get; }
      public void SetImages(bool preview, bool timeline);
      public void Tick(VideoTab? tab);
      public string Kept { get; }
      public bool CanDetectIdle { get; }
      public string NoticeText { get; }
  }
  // ToolbarPanel
  public void UpdateVideo(VideoTab? tab);   // labels, disabled states, progress ("Cancel 42 %"), position text
  ```

**`video.rml` layout.** The body is `<body class="panel-doc video-doc" data-model="video">`. Copy `song.rml`'s pattern:
`body { pointer-events: none }`, with `auto` only on the interactive blocks.
- `#video-empty` is the empty state from Task 4, plus `<div id="video-recent">` rows (`data-for="r : recent"`). It shows
  when `data-if="!has_tab"`.
- `#video-main` (`data-if="has_tab"`) is a flex column holding, in order:
  - `#video-upper`, a flex row with `flex: 1 1 auto`, holding:
    - `#video-preview` (`class="canvas"`, `flex: 1 1 auto`, background `#0b0c0f`), with a centred
      `<img id="video-preview-image"/>`;
    - `#video-split-v`, a 5 dp splitter (`cursor: ew-resize`, `pointer-events: auto`);
    - `#video-transcript` (`data-if="show_transcript"`, width set from code, `pointer-events: auto`,
      `overflow-y: auto`), which Task 12 fills.
  - `#video-notice` (`data-if="notice != ''"`): one line in `.notice` styling (amber on `#3a2a0d`), showing
    `{{notice}}`.
  - `#video-split-h`, a 5 dp splitter (`cursor: ns-resize`, `pointer-events: auto`).
  - `#video-timeline` (`class="canvas"`, height set from code, default 180 dp), with
    `<img id="video-timeline-image"/>` at 100% × 100%.

**`#video-tools` in `toolbar.rml`.** `ToolbarPanel.UpdateVideo` keeps these current, writing strings only when they
change. In order:
- `#video-play` (`data-command="video.play"`), whose icon switches between `player-play` and `player-pause` by class;
- `#video-stop` (`video.stop`, icon `player-stop`);
- `#video-pos`, monospaced: `"1:02.3 / 6:00.0"`, built from `VideoFormat.Clock` of the position and the duration;
- a separator;
- `#video-transcribe`, `#video-fillers`, `#video-silence` and `#video-idle`, text buttons that read "Cancel n %" while
  their job runs;
- a flex spacer;
- `#video-kept`, dim text: "Kept 4:12.3 of 6:00.0";
- `#video-export` (`button.accent`).

Each button carries `data-command` with its `video.*` id.
- With no active tab, every button is disabled (class `disabled`, and the command is ignored).
- `#video-idle` is also disabled when there is no sidecar. Its tooltip then reads "Needs a Mainframe Engine recording
  (its .mfcap.json sidecar)".
- `#video-fillers` stays enabled without a transcript, because it transcribes first.

**`VideoView`**, modelled on `SongView`.
- `OnProcess`:
  1. `PollJobs`.
  2. Switch to `Session.ActiveTab as VideoTab`, but only when `Workspace.Mode == Video`; otherwise null.
  3. Call `VideoPanel.Tick(tab)` and `Toolbar.UpdateVideo(tab)`.
  4. Set `tab.View.Width = TimelineRect.Width`.
  5. `Place` the timeline viewport and canvas, as `SongView.Place` does. Keep the preview viewport on `Always` while it
     is shown.
  6. `Publish` both textures: `ui.RegisterTexture(TimelineTexture, tab.TimelineViewport)` and
     `ui.RegisterTexture(PreviewTexture, tab.Preview.Viewport)`.
- `Switch` cancels any gesture, unregisters both textures, and calls `SetImages(false, false)`. It sets
  `tab.Controller.ShowSegmentMenu` to:
  ```csharp
  Workspace.Popup.Show([new MenuItem("Delete", "edit.delete", "Del", Icon: "trash")], TimelineRect.X + x, TimelineRect.Y + y, onCommand: id => { if (id == "edit.delete") tab.Controller.DeleteHovered(); })
  ```
- `OnInput` mirrors `SongView.OnInput`, with one area, the timeline:
  - left press → `Controller.Press`; left release with a gesture active → `Controller.Release`;
  - motion → `Controller.Drag` during a gesture, else `Controller.Hover`;
  - right press → `Controller.RightClick`; wheel → `Controller.Wheel`;
  - every handled event calls `SetInputAsHandled`, and a drag that leaves the area keeps routing;
  - dragging `#video-split-v` or `#video-split-h` resizes through the layout setters, saved on release.
- `HandleKey` returns false unless a video tab is active. Space toggles play, Home seeks to 0, and every other key goes
  to `Controller.Key`.
- `DetectSilence(tab)`:
  - If `tab.SilenceJob` is running, cancel it.
  - Otherwise, if `tab.Ffmpeg` is null, `ReportError` "ffmpeg not found".
  - Otherwise, start
    `tab.SilenceJob = VideoJob<IReadOnlyList<SilenceSpan>>.Start("silence", (p, ct) => new SilenceDetector(ffmpeg).DetectAsync(tab.Document.SourcePath, tab.Document.Duration, settings, p, ct))`.
    Here `settings` is `DetectionSettings.Default` until Task 15 reads it from Video Settings.
- `PollJobs`, when the silence job completes, on the main thread:
  - on success, `tab.Notice = tab.Document.ApplySilence(spans, settings)`;
  - on cancel, the notice "Silence detection cancelled.";
  - on failure, `ReportError("Detect Silence failed", e)`.

**Commands.**
- `video.detect_idle` sets `tab.Notice = tab.Document.DetectIdleGameplay(GameplaySettings.Default)` when the recording
  has a capture, or "This recording has no Mainframe Engine sidecar." when it does not.
- `video.detect_silence` calls `VideoView.DetectSilence`.
- `video.toggle_transcript` and `view.output` flip their layout setting and call `ApplyLayout()`. In video mode,
  `view.output` also sets `OutputPanel.Visible`.

- [ ] **Step 1: Write the failing tests.** `VideoWorkspaceTests.cs` runs on `HeadlessEditor`, with a `.mfcut` built as
  in Task 10:
  - `OpeningAVideoShowsTheVideoScreen`: after opening and `Tick(2)`, `VideoPanel.Visible` is true and the toolbar's
    `#video-tools` has no `hidden` class.
  - `DetectIdleIsDisabledWithoutASidecar`: `VideoPanel.CanDetectIdle` is false.
  - `DetectIdleCutsAndSetsTheNotice`: write `VideoDocumentTests.Sidecar` beside the dummy video before opening. After
    `Commands.Execute("video.detect_idle")`, `tab.Notice` starts with "Cut ", and `edit.undo` reverts the cut.
  - `SpaceTogglesPlaybackAndDeleteDissolvesTheHoveredRegion`: `Key(Key.Space)` starts playing. Then
    `CreateSection(10, 20, 10)`, set `State.HoverSegment = 1`, press `Key(Key.Delete)`, and the segment count drops.
  - `TheKeptLabelFollowsEdits`: "Kept 1:00.0 of 1:00.0", then "Kept 0:50.0 of 1:00.0" after a 10 s cut.
  - `HidingTheTranscriptGivesThePreviewTheWidth`: after `video.toggle_transcript` and a `Tick`, `TranscriptRect.Width`
    is 0 and `PreviewRect.Width` has grown.

- [ ] **Step 2: Implement, run the editor suite (PASS, with no RmlUi warnings), run the gates, and commit.** Commit
  message: "Show the video editing screen: preview, timeline, toolbar, notices, Detect Idle and Detect Silence".

---

### Task 12: `TranscriptPanel` — edit by text

**Files:**
- Create: `MainframeEngine.Editor/Src/Video/TranscriptMarkup.cs` (pure).
- Create: `MainframeEngine.Editor/Src/Video/TranscriptPanel.cs`, the behaviour of `#video-transcript` inside
  `VideoPanel`'s document.
- Modify: `MainframeEngine.Editor/Src/Video/VideoPanel.cs`, which owns a `TranscriptPanel`; `video.rml`, with styles
  for `.w`, `.w.cut`, `.w.cur`, `.w.sel` and `.para`.
- Test: `Tests/MainframeEngine.Editor.Tests/Video/TranscriptMarkupTests.cs` and `TranscriptPanelTests.cs`.

**Interfaces:**
- Consumes: `VideoDocument.Transcript`, `CutWords`, `TranscriptView.CutStates`, `VideoTab.Seek` and `CurrentWord`.
- Produces:
  ```csharp
  public static class TranscriptMarkup
  {
      public const double ParagraphGapSeconds = 2.0;
      public static string Build(IReadOnlyList<Word> words, IReadOnlyList<bool> cut);  // RML: <div class="para"><span id="w-0" class="w" data-i="0">so</span> …</div>
      public static string Escape(string text);
  }
  public sealed class TranscriptPanel   // not a document: drives #video-transcript inside VideoPanel's document
  {
      public void Attach(RmlDocument document);       // listeners: mousedown / mousemove / mouseup / contextmenu on #video-transcript
      public void Update(VideoTab? tab);               // rebuilds only when Transcript or Version changed; moves the current-word class
      internal void Press(int word, EditorModifiers modifiers);   // the gesture logic, testable without RmlUi
      internal void Drag(int word);
      internal void Release();
  }
  ```

**Behaviour**, matching Cutback's `TranscriptControl`:
- **Rebuilding.** When `Document.Transcript` or `Document.Version` changes, rebuild the inner RML of
  `#video-transcript-words` with `TranscriptMarkup.Build(words, TranscriptView.CutStates(words, segments))`.
  - A gap of 2.0 s or more starts a new paragraph.
  - Each word is `<span id="w-{i}" class="w{ cut}" data-i="{i}">{escaped text}</span>`, followed by a space.
  - With no transcript, the panel shows "No transcript yet. Transcribe to edit by text." (`.hint`).
- **The current word** gets class `cur`, and is scrolled into view while playing (`ScrollIntoView(false)` when it
  leaves the visible area). Moving the class touches only the old span and the new one.
- **Mousedown** on a `.w`:
  - with Cmd/Ctrl held, `tab.Seek(word.Start)` and stop;
  - otherwise, start a run anchored at that word.
- **Mousemove** while pressed extends the run and marks it with class `sel`.
- **Mouseup** calls `Document.CutWords(min, max, anchor)`. That is one undo step, taking the opposite state of the
  anchor word: clicking a kept word cuts it, and clicking a struck word restores it.
- **Right-click** on a word opens `Workspace.Popup` with two items:
  - "Play from here" (`player-play`): `tab.Seek(start)`, then `TogglePlay` if paused;
  - "Cut" or "Restore", for this word only. Use the `scissors` icon if the atlas has it; otherwise the nearest
    existing icon.

- [ ] **Step 1: Write the failing tests**

`TranscriptMarkupTests.cs`:

```csharp
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class TranscriptMarkupTests
{
    [Fact]
    public void WordsBecomeSpansWithTheirCutState()
    {
        Word[] words = [new("so", 1.0, 1.3, 0.9), new("um", 1.4, 1.8, 0.8)];
        Assert.Equal(
            "<div class=\"para\"><span id=\"w-0\" class=\"w\" data-i=\"0\">so</span> <span id=\"w-1\" class=\"w cut\" data-i=\"1\">um</span> </div>",
            TranscriptMarkup.Build(words, [false, true]));
    }

    [Fact]
    public void ALongPauseStartsANewParagraph()
    {
        Word[] words = [new("a", 0, 0.5, 1), new("b", 2.6, 3.0, 1)];
        Assert.Equal(2, TranscriptMarkup.Build(words, [false, false]).Split("<div class=\"para\">").Length - 1);
    }

    [Fact]
    public void TextIsEscaped() => Assert.Equal("&lt;b&gt; &amp; &quot;", TranscriptMarkup.Escape("<b> & \""));
}
```

`TranscriptPanelTests.cs` runs on `HeadlessEditor` with an open video tab whose transcript is
`VideoDocumentTests.Words()`:
- `ClickingAWordCutsIt`: `Press(1, None)` then `Release()` cuts the word at 1.6 s, as one undo step.
- `ClickingAStruckWordRestoresIt`: cut a word, click it again, and it is restored.
- `DraggingAcrossWordsCutsTheRun`: `Press(0)`, `Drag(2)`, `Release()` cuts 1.0–2.2 s as one step.
- `CommandClickPlaysFromTheWord`: `Press(2, Command)` sets `tab.State.Playhead` to 2.0 and cuts nothing.
- `TheMarkupRebuildsAfterUndo`: cut, `edit.undo`, `Update(tab)`, and `#w-1` has no `cut` class.
- `ANewTranscriptShowsThePlaceholderUntilTranscribed`.

- [ ] **Step 2: Implement, run (PASS), run the gates, and commit.** Commit message: "Edit by transcript: a word panel with cut, restore, run selection and play from word".

---

### Task 13: Transcribe, Remove Filler Words, Export Transcript

**Files:**
- Modify:
  - `MainframeEngine.Editor/Src/Video/VideoView.cs`: `Transcribe`, the `RemoveFillers` flow, and job polling.
  - `MainframeEngine.Editor/Src/EditorCommands.cs`: `video.transcribe`, `video.remove_fillers` and
    `video.export_transcript`.
  - `MainframeEngine.Editor/Src/EditorWorkspaceOptions.cs`: `VideoModelsDirectory`, defaulting to
    `Path.Combine(Path.GetDirectoryName(EditorSettings.DefaultPath)!, "models")`; tests set a temp folder.
- Test: `Tests/MainframeEngine.Editor.Tests/Video/TranscriptionFlowTests.cs`, with a fake transcriber and no model.

**Interfaces:**
- Consumes: `ModelStore(string)` (`IsDownloaded`, `EnsureAsync`); `WhisperModelInfo.Parse` and `For`;
  `WhisperTranscriber(ffmpeg, modelPath, model).TranscribeAsync(path, progress, ct)`; `VideoDocument.SetTranscript`,
  `RemoveFillerWords` and `TranscriptText`.
- Produces:
  ```csharp
  // VideoView
  public Func<FfmpegLocation, string, WhisperModel, ITranscriber> TranscriberFactory { get; set; }   // tests swap it
  public void Transcribe(VideoTab tab, bool thenRemoveFillers = false);   // a second call while running cancels
  public void RemoveFillers(VideoTab tab);   // transcribes first when there is no transcript
  ```

**Behaviour** (ports Cutback's `TranscribeCoreAsync`, `RemoveFillerWordsAsync` and `ExportTranscriptAsync`):
- **`Transcribe`.**
  - `model = WhisperModelInfo.Parse(Settings.VideoWhisperModel)`, which is `"base.en"` until Task 15.
  - `store = new ModelStore(Options.VideoModelsDirectory)`.
  - The job:
    ```csharp
    tab.TranscribeJob = VideoJob<IReadOnlyList<Word>>.Start("transcribe", async (p, ct) =>
    {
        var modelPath = await store.EnsureAsync(model, phase1, ct);
        return await TranscriberFactory(ffmpeg, modelPath, model).TranscribeAsync(tab.Document.SourcePath, phase2, ct);
    });
    ```
  - Progress is split: when the model is missing, the download is 0–0.5 and transcription the rest; otherwise
    transcription takes the whole range.
  - The toolbar button reads "Downloading model n %", then "Cancel n %".
  - **On completion** (main thread): `tab.Document.SetTranscript(words)`, then the notice "Transcribed N words." or
    "No speech was recognised.". Show the transcript panel if it is hidden. If `thenRemoveFillers` was set, run the
    filler step.
  - **On cancel:** the notice "Transcription cancelled.".
  - **On failure:** `ModelDownloadException` and `ModelLoadException` go to `ReportError("Transcription failed", e)`,
    keeping Cutback's messages.
- **`RemoveFillers`.** With no transcript, call `Transcribe(tab, thenRemoveFillers: true)`. Otherwise set
  `tab.Notice = tab.Document.RemoveFillerWords(Settings.VideoFillerWords, DetectionSettings.Default)`. Until Task 15
  adds the setting, use `FillerDetector.DefaultWords`.
- **`video.export_transcript`.**
  - Opens a save picker (`["*.txt", "*.srt"]`, defaulting to `<name>.txt` beside the source).
  - Writes `Document.TranscriptText(srt)` as UTF-8 without a BOM, then sets the notice `"Exported {file}."`.
  - An `IOException` or `UnauthorizedAccessException` goes to `ReportError`.
  - Disabled when there is no transcript.

- [ ] **Step 1: Write the failing tests** (`TranscriptionFlowTests`, on `HeadlessEditor`):
  - `TranscribingStoresTheWordsAndMarksTheProjectDirty`:
    - `TranscriberFactory` returns a fake `ITranscriber` that yields `VideoDocumentTests.Words()`.
    - Put a model file in place first so nothing downloads: create `{models}/ggml-base.en.bin` at the size
      `WhisperModelInfo.For(BaseEn)` expects, or whatever `ModelStore.IsPlausiblyComplete` checks.
    - Pump `Tick` until the job finishes. The transcript has 4 words, `IsDirty` is true, and the notice is "Transcribed
      4 words.".
  - `RemoveFillersTranscribesFirstWhenThereIsNoTranscript`: the transcript gets set and a filler cut exists.
  - `CancellingTranscriptionLeavesNoTranscript`.
  - `ExportTranscriptWritesTheEditedText`: accept the `FilePicker` the way the existing Save As tests do, or call the
    internal writer `EditorCommands.WriteTranscript(tab, path)` directly.

- [ ] **Step 2: Implement, run (PASS), run the gates, and commit.** Commit message: "Transcribe recordings with Whisper, remove filler words, export the transcript".

---

### Task 14: Export Video

**Files:**
- Create: `MainframeEngine.Editor/Src/Video/VideoExport.cs` (pure helpers), `MainframeEngine.Editor/Src/Video/ExportVideoDialog.cs`
  and `MainframeEngine.Editor/Assets/Editor/video_export.rml`.
- Modify:
  - `MainframeEngine.Editor/Src/EditorWorkspace.cs`: add `ExportVideoDialog` to the `DialogLayer`, and include it in
    `IsDialogOpen`.
  - `VideoView.cs`: export polling, and one `TempSession`, disposed in `OnExitTree`.
  - `EditorCommands.cs`: `video.export`.
- Test: `Tests/MainframeEngine.Editor.Tests/Video/VideoExportTests.cs`.

**Interfaces:**
- Produces:
  ```csharp
  public static class VideoExport
  {
      public static string DefaultPath(string sourcePath, Func<string, bool> exists);   // <name>-cut.mp4, then -cut-2, -cut-3 …
      public static ExportRequest Request(VideoDocument document, string outputPath, ExportMode mode);
      public static bool FastAllowed(string outputPath);                                 // false for .webm
  }
  public sealed class ExportVideoDialog : EditorDocument
  {
      public void Open(VideoTab tab);
      public string OutputPath { get; set; }
      public bool Fast { get; set; }
  }
  ```

**Behaviour:**
- **The dialog** (`video_export.rml`) is a `backdrop` plus `.dialog`, styled like `editor_settings.rml`. It holds:
  - the title "Export Video";
  - a summary line, "Kept 4:12.3 of 6:00.0 · 23 cuts";
  - an output path text field with Browse, which opens `FilePicker` Save with `["*.mp4", "*.mov", "*.mkv", "*.webm"]`;
  - two radio rows: "Precise (re-encode, frame-accurate)", the default; and "Fast (copy, approximate: cuts snap back to
    keyframes)", disabled with a hint when the output is `.webm`;
  - Export and Cancel buttons.
- **Export:**
  1. Pause the preview.
  2. Start the job:
     ```csharp
     tab.ExportJob = VideoJob<string>.Start("export", async (p, ct) =>
     {
         await new Exporter(ffmpeg, _temp).ExportAsync(VideoExport.Request(...), new Progress<ExportProgress>(x => p.Report(x.Fraction)), ct);
         return path;
     });
     ```
     If `Progress<T>` would post to a synchronisation context, use a small sink class instead, as `VideoJob` does.
  3. Close the dialog.
  - While the job runs, the toolbar's Export reads "Cancel n %", and pressing it cancels.
- **When the job finishes:**
  - **Success:** `Log.Info("[Video] Exported {path}")`, then a `Message` titled "Export finished" with the file name and
    folder, and the buttons ["Show in Folder", "Close"]. Show in Folder uses `OsShell.Reveal`.
  - **Cancel:** the notice "Export cancelled.".
  - **Failure:** `ReportError("Export failed", e)`. `FfmpegException` already carries the stderr tail, or the
    no-encoder message.
- **Nothing to export:** `video.export` with nothing kept sets the notice "Every segment is removed; there is nothing to
  export.". With no ffmpeg it goes to `ReportError`.

- [ ] **Step 1: Write the failing tests**

```csharp
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Editor.Tests.Video;

public sealed class VideoExportTests
{
    [Fact]
    public void TheDefaultIsNextToTheRecordingAndNeverOverwrites()
    {
        var taken = new HashSet<string> { Path.Combine("/r", "run-cut.mp4"), Path.Combine("/r", "run-cut-2.mp4") };
        Assert.Equal(Path.Combine("/r", "run-cut-3.mp4"), VideoExport.DefaultPath(Path.Combine("/r", "run.mp4"), taken.Contains));
        Assert.Equal(Path.Combine("/r", "run-cut.mp4"), VideoExport.DefaultPath(Path.Combine("/r", "run.mp4"), _ => false));
    }

    [Fact]
    public void TheRequestCarriesTheModeAndTheSourceSizeForTheEncoder()
    {
        var doc = VideoDocument.FromProject(VideoDocumentTests.Project("/r/run.mp4"), null, null, []);
        doc.Editor.CreateSection(10, 20, anchor: 10);
        var request = VideoExport.Request(doc, "/r/run-cut.mp4", ExportMode.Fast);
        Assert.Equal(ExportMode.Fast, request.Mode);
        Assert.Equal((1920, 1080, 60.0), (request.Width, request.Height, request.FrameRate));
        Assert.Equal(3, request.Segments.Count);
    }

    [Theory]
    [InlineData("/r/a.mp4", true)]
    [InlineData("/r/a.webm", false)]
    public void FastExportIsNotOfferedForWebm(string path, bool allowed) => Assert.Equal(allowed, VideoExport.FastAllowed(path));
}
```

Also add `ExportVideoDialogTests` (on `HeadlessEditor`):
- `TheDialogDefaultsToPreciseNextToTheRecording`;
- `ChoosingWebmTurnsFastOff`;
- `ExportingWithEverythingCutOnlyWarns`.

- [ ] **Step 2: Implement, run (PASS), run the gates, and commit.** Commit message: "Export trimmed recordings: Precise or Fast, with progress and Show in Folder".

---

### Task 15: Video Settings, and ffmpeg for Play and Record

**Files:**
- Modify:
  - `MainframeEngine.Editor/Src/Settings/EditorSettings.cs`: the fields below, with clamps, load, save and clone.
  - `MainframeEngine.Editor/Src/UI/EditorSettingsDialog.cs` and `Assets/Editor/editor_settings.rml`: a "Video"
    section. Make `.dialog-body` scroll (`overflow-y: auto`) and keep the dialog's fixed size.
  - `EditorCommands.cs`: `video.settings` opens the dialog scrolled to `#video-section`.
  - The consumers:
    - `VideoView`: the silence settings, filler words and Whisper model.
    - `VideoDocument` callers: the gameplay settings, passed at the call site.
    - `EditorCommands.OpenVideo`: the history limit.
    - Applying settings updates `EditHistory.Limit` on open tabs.
  - The place in `MainframeEngine.Editor/Src/Play/…` where `--frame-capture` is added (find it with
    `grep -rn "frame-capture" MainframeEngine.Editor/Src/Play`). When `VideoFfmpegPath` is set, also pass
    `--ffmpeg <VideoFfmpegPath>` as a host flag, before `--editor-port`.
- Test: `Tests/MainframeEngine.Editor.Tests/Video/VideoSettingsTests.cs`, and extend the play argument tests.

**Settings.** Names, defaults and clamps come from Cutback's `AppSettings`:

| Property | Default | Clamp |
|---|---|---|
| `VideoFfmpegPath` (from Task 10) | null | trimmed; empty → null |
| `VideoWhisperModel` | `"base.en"` | must be a `WhisperModelInfo` key, else `"base.en"` |
| `VideoFillerWords` | `FillerDetector.DefaultWords` | trimmed, deduped, empty entries dropped; an empty list → the defaults |
| `VideoSilenceThresholdDb` | −34 | −80…0 |
| `VideoMinSilenceMs` | 400 | 50…5000 |
| `VideoPaddingMs` | 60 | 0…500 |
| `VideoMinKeepMs` | 120 | 0…2000 |
| `VideoGameplayMinIdleMs` | 3000 | 500…60000 |
| `VideoGameplayPaddingMs` | 500 | 0…2000 |
| `VideoGameplayMinKeepMs` | 1000 | 0…10000 |
| `VideoGameplayMarkerWindowMs` | 1500 | 0…10000 |
| `VideoUndoHistoryLimit` | 100 | 10…1000 |

Two convenience properties:
- `public DetectionSettings VideoDetection => new(VideoPaddingMs, VideoMinSilenceMs, VideoSilenceThresholdDb, VideoMinKeepMs);`
- `public GameplaySettings VideoGameplay => new(...)`.

**The Video section** (`<div id="video-section">`) contains:
- ffmpeg: a text input, with the hint "Folder or file; blank uses MAINFRAME_FFMPEG, then PATH".
- Whisper model: a select whose options show the key and size, for example "base.en (148 MB)".
- Filler words: comma-separated text.
- The four silence numbers and the four gameplay numbers, each a text input with the clamp applied in the setter.
- The undo history limit.

- [ ] **Step 1: Write the failing tests:**
  - `VideoSettingsRoundTripAndClamp`: save, load, and check the clamps, including an out-of-range value and an unknown
    model key.
  - `DetectSilenceUsesTheSettings`: with padding 0, applying spans makes cuts that exactly equal the spans.
  - `TheUndoLimitAppliesToOpenTabs`.
  - `PlayPassesTheFfmpegSettingToTheGame`: extend the existing launch-argument tests. With the setting, `--ffmpeg /opt/ff`
    comes before `--editor-port`; without it, `--ffmpeg` is absent.

- [ ] **Step 2: Implement, run (PASS), run the gates, and commit.** Commit message: "Add Video Settings and pass the ffmpeg setting to recorded games".

---

### Task 16: "Open in Video Editor" in the recording dialog

**Files:**
- Modify:
  - `MainframeEngine.Editor/Src/Play/RecordingNotifier.cs`: a new `Action<string> openInVideoEditor` constructor
    parameter.
  - `MainframeEngine.Editor/Src/EditorWorkspace.cs`, where the notifier is constructed: pass
    `path => Commands.OpenVideo(path)`.
  - `Tests/MainframeEngine.Editor.Tests/Play/RecordingNotifierTests.cs`.

**Behaviour:**
- The Saved dialog's buttons become `["Open in Video Editor", "Open in Cutback", "Show in Folder", "Close"]`, with
  `DefaultButton = 0` and `CancelButton = 3`.
- The callback maps `0` → `openInVideoEditor(path)`, `1` → `OpenInCutback(path)` and `2` → `reveal(path)`.
- The class summary says the video editor is the default and Cutback stays available.

Ruling: keep "Open in Cutback". The user chose to leave the Cutback app untouched, and whether to drop it waits on
their decision about brogan89/Cutback#7. If #7 is closed, removing the button is a one-line follow-up.

- [ ] **Step 1: Update the tests first:**
  - the harness passes a delegate that records the path;
  - the buttons assertion uses the new list;
  - existing button indices shift by one;
  - add `OpenInVideoEditorOpensTheRecordingInTheEditor`, asserting that callback 0 records the path.
- [ ] **Step 2: Implement, run (PASS), run the gates, and commit.** Commit message: "Offer Open in Video Editor when a recording is saved".

---

### Task 17: QA fixture and script, the video-mode golden, docs

**Files:**
- Create: `build/make-video-fixture.sh`, `Tests/QA/Fixtures/video/sample.mp4`,
  `Tests/QA/Fixtures/video/sample.mp4.mfcap.json` and `Tests/QA/video.qa`.
- Modify, for the golden:
  - `MainframeEngine.Editor/Src/EditorCommandLine.cs`: a `video-empty` golden.
  - `MainframeEngine.Editor/Src/Qa/EditorSmokeRun.cs`: a `RunGolden` case.
  - `Tests/MainframeEngine.RenderTests/EditorRenderTests.cs`: a test.
  - `Tests/MainframeEngine.RenderTests/Goldens/{moltenvk,lavapipe}/editor-video-empty_frame0040.png`.
- Docs:
  - Move `docs/design/future/video-editor.md` to `docs/design/video-editor.md`, rewritten as built.
  - Add `memory/decisions/0191-video-editing-mode.md`.
  - `docs/design/editor.md`: an "Editor modes" section and the shortcuts table.
  - `docs/design/recording.md`: the dialog's buttons.
  - `CLAUDE.md` and `memory/context/progress-log.md`.

- [ ] **Step 1: The fixture**

`build/make-video-fixture.sh`:

```bash
#!/usr/bin/env bash
# Regenerates Tests/QA/Fixtures/video/sample.mp4: 12 s of 320x180 test pattern with a 440 Hz tone that drops out from
# 4 to 6 s (silence to detect). Its sidecar (committed beside it, hand-written) marks 7-9 s as loading and a marker at
# 10 s. Small enough to commit.
set -euo pipefail
out="$(cd "$(dirname "$0")/.." && pwd)/Tests/QA/Fixtures/video/sample.mp4"
mkdir -p "$(dirname "$out")"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=s=320x180:r=30:d=12" \
  -f lavfi -i "sine=f=440:r=48000:d=12" \
  -af "volume=enable='between(t,4,6)':volume=0" \
  -c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p -g 30 -c:a aac -b:a 64k -movflags +faststart -shortest "$out"
ls -l "$out"
```

If the local ffmpeg has no libx264, use `-c:v h264_videotoolbox -b:v 300k` instead.

`sample.mp4.mfcap.json` follows the schema of `Tests/MainframeEngine.Tests/Recording/Fixtures/sample.mfcap.json`:
- `"video": "sample.mp4"`, `"mode": "live"`, `"frameRate": 30`, `"width": 320`, `"height": 180`, `"durationSeconds": 12.0`;
- `input`: `[0,3]` and `[9.5,12]`;
- `loading`: `[7,9]`;
- `markers`: `[{ "time": 10.0, "label": "qa marker" }]`.

The MP4 must stay under 400 KB.

- [ ] **Step 2: `Tests/QA/video.qa`** (manual only, like `record.qa`)

```
# Video editing mode: open a recording, detect, transcribe, edit, play, export; each step captured for review.
# Needs a display, ffmpeg, and network for the first tiny.en download (just qa-editor Tests/QA/video.qa); not in CI.
wait-for project 300
mode video
capture video-01-empty
open Tests/QA/Fixtures/video/sample.mp4
wait 120
capture video-02-opened
command video.detect_idle
wait 10
capture video-03-idle
command video.detect_silence
wait 180
capture video-04-silence
command video.transcribe
wait 600
capture video-05-transcript
command edit.undo
wait 10
capture video-06-undo
command video.play
wait 90
capture video-07-playing
command video.play
mode game
capture video-08-game-mode
mode video
command file.close
cancel
quit
```

Before `command video.transcribe`, the script must select `tiny.en`. Add a `video set <setting> <value>` verb for that,
or reuse `set #id text` on the settings dialog.

Run: `just qa-editor Tests/QA/video.qa`

**Look at every capture.** What each should show:
- `01`: the empty state.
- `02`: the preview shows the test pattern; the timeline shows the waveform, its 4–6 s dropout, and the marker.
- `03`: 7–9 s is grey with the reason "loading".
- `04`: roughly 4–6 s is grey.
- `05`: the transcript panel has words, or "No speech was recognised." for the pure tone, which is acceptable. Note
  which in the report.
- `06`: the last edit is undone.
- `07`: the playhead has moved and skipped the cut regions.
- `08`: the game layout is back.

The controller runs this step itself.

- [ ] **Step 3: The `video-empty` golden**

1. In `EditorCommandLine`, add `"video-empty"` to the `--smoke-golden` switch, its error text and `Usage`.
2. In `EditorSmokeRun.RunGolden`, add
   `case ("video-empty", 3): workspace.Session.CloseAll(); workspace.SetMode(EditorMode.Video); workspace.Output.Clear(); break;`.
3. In `EditorRenderTests`, add a test copied from `ProjectManagerMatchesGolden`, with `"video-empty"` and
   `Output("editor-video-empty")`.
4. Record the MoltenVK golden with
   `UPDATE_GOLDENS=1 dotnet test Tests/MainframeEngine.RenderTests --filter FullyQualifiedName~VideoEmpty`.
5. Record the lavapipe golden with `just render-tests-linux --filter FullyQualifiedName~VideoEmpty`. It needs Docker;
   if Docker is not running, report NEEDS_CONTEXT.
6. Look at both images.

- [ ] **Step 4: Docs**

`memory/decisions/0191-video-editing-mode.md`:
- **Context:** promo footage needs trimming, and the editor already records.
- **Decision:**
  - one workspace with an `EditorMode` (Game, Video, and Audio later);
  - Cutback's MIT logic copied into the editor-only `MainframeEngine.Video`, not consumed as packages;
  - Whisper.net as a dependency;
  - ffmpeg run as a child process (ADR 0190);
  - `.mfcut` files using Cutback's schema.
- **Alternatives:**
  - separate workspaces per mode, which duplicates the shell;
  - NuGet packages, which need a publish step and suffer version skew;
  - a Clips tab only, which is not a real mode;
  - G16 playback, which targets AV1/WebM for games.
- **Consequences:**
  - three new managed dependencies (MainframeEngine.Video, Whisper.net, Whisper.net.Runtime);
  - the preview re-uploads its texture every frame;
  - the first transcription downloads a model.

`docs/design/video-editor.md`:
- Rewrite the spec in the present tense, as built.
- Keep the four SVGs, with their paths fixed to `../images/`.
- Delete the `future/` copy, and update links to it from `editor.md`, `recording.md` and `docs/milestones.md` (if that
  lists future proposals).

`docs/design/editor.md`:
- A short "Editor modes" section covering the toggle, what changes, and the DAW mode coming later, with a pointer to
  `video-editor.md`.
- The video shortcuts, added to the keyboard shortcuts table.

`docs/design/recording.md`: the dialog's buttons.

`CLAUDE.md` (engine): one paragraph on the video mode. It covers:
- `MainframeEngine.Video`: editor-only, MIT, copied from Cutback; its tests never run ffmpeg or load a model;
- `MainframeEngine.Editor/Src/Video/`;
- the modes section in `editor.md`.

`memory/context/progress-log.md`: a "Resume here" entry covering the branch, the gates and the QA result. Its parked
items:
- dragging files in from the OS;
- the per-frame texture re-upload (G16's `VideoTexture` later);
- the DAW mode;
- crash recovery for video tabs (`WriteRecoveryCopies` covers scenes only).

- [ ] **Step 5: Final gates and commit**

```bash
just build && just test && just format-check
dotnet build MainframeEngine.slnx -c Release -warnaserror -p:CompileShaders=false
just test-render
git add -A build Tests docs memory CLAUDE.md MainframeEngine.Editor
git commit -m "Document the video editing mode; add its QA script, fixture recording and golden

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Hand-off

Push and open the PR only when the user says so. It targets `main`.
