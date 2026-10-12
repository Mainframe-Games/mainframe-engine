using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The New Project wizard (E4): name, parent folder and engine source, validated as you type, the .NET SDK check
/// (with the download link), then <c>dotnet new mfgame</c> through the <see cref="ProjectCreator"/> with live output;
/// the new project opens when it is done. Cancel stops a running creation.
/// </summary>
/// <remarks>
/// The engine source defaults to the MainframeEngine NuGet package at this editor's version (a release editor needs no
/// engine checkout; the template ships next to it). Development builds (<c>0.0.0-dev</c>) have no package and use an
/// engine checkout, which a release editor can also choose (for engine work).
/// </remarks>
public sealed class NewProjectDialog : EditorDocument
{
    private const int MaxLogLines = 12;

    private readonly Queue<string> _log = new();
    private RmlDataModel? _model;
    private string _name = "MyGame";
    private string _location = DefaultLocation;
    private string _enginePath = "";
    private EngineSource _source = NewProjectValidation.IsPackageVersion(EngineInfo.Version) ? EngineSource.Package : EngineSource.Checkout;
    private string _error = "";
    private string _logText = "";
    private string _sdkText = "";
    private bool _busy;
    private Task<DotnetSdkInfo>? _sdkCheck;
    private Task<ProjectCreateResult>? _creation;
    private CancellationTokenSource? _cancel;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _incoming = new();

    public NewProjectDialog(EditorWorkspace workspace)
        : base(workspace, "new_project.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>Where new projects go by default: <c>~/MainframeProjects</c>.</summary>
    public static string DefaultLocation =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MainframeProjects");

    public string ProjectName
    {
        get => _name;
        set
        {
            _name = value ?? "";
            Validate();
        }
    }

    public string Location
    {
        get => _location;
        set
        {
            _location = value ?? "";
            Validate();
        }
    }

    public string EnginePath
    {
        get => _enginePath;
        set
        {
            _enginePath = value ?? "";
            Validate();
        }
    }

    /// <summary>The package (the editor's version, when it is a release) or an engine checkout (<see cref="EnginePath"/>).</summary>
    public EngineSource EngineSource
    {
        get => _source;
        set
        {
            _source = value;
            Validate();
        }
    }

    /// <summary>True when this editor is a release, so a MainframeEngine package of its version exists.</summary>
    public static bool PackageAvailable => NewProjectValidation.IsPackageVersion(EngineInfo.Version);

    /// <summary>The current validation or creation error (empty when none).</summary>
    public string Error => _error;

    public bool IsCreating => _busy;

    /// <summary>The finished creation's result (null before).</summary>
    public ProjectCreateResult? LastResult { get; private set; }

    /// <summary>The SDK the dialog found (null while checking).</summary>
    public DotnetSdkInfo? Sdk { get; private set; }

    protected override void OnReady()
    {
        _model = CreateDataModel("new_project")
            // Typing validates after the UI update that reported it (never inside RmlUi's own update).
            .Bind("name", this, static d => d._name, static (d, v) =>
            {
                d._name = v ?? "";
                d.ValidateLater();
            })
            .Bind("location", this, static d => d._location, static (d, v) =>
            {
                d._location = v ?? "";
                d.ValidateLater();
            })
            .Bind("engine", this, static d => d._enginePath, static (d, v) =>
            {
                d._enginePath = v ?? "";
                d.ValidateLater();
            })
            .Bind("source", this, static d => (int)d._source)
            .Bind("package_available", this, static _ => PackageAvailable)
            .Bind("package_tooltip", this, static _ => PackageAvailable
                ? $"MainframeEngine {EngineInfo.Version} from NuGet — the engine package of this editor's version; no engine checkout needed"
                : $"MainframeEngine package — not available: this editor is a development build ({EngineInfo.Version})")
            .Bind("package_text", this, static _ => $"MainframeEngine {EngineInfo.Version}")
            .Bind("error", this, static d => d._error)
            .Bind("log", this, static d => d._logText)
            .Bind("sdk", this, static d => d._sdkText)
            .Bind("busy", this, static d => d._busy)
            .Bind("target", this, static d => d.TargetText())
            .Event("browse_location", _ => BrowseLocation())
            .Event("browse_engine", _ => BrowseEngine())
            .Event("set_source", e => SetSourceLater((EngineSource)e.GetArgument(0).GetInt32()))
            .Event("create", _ => Create())
            .Event("cancel", _ => Cancel());
    }

    private string TargetText() => _name.Length == 0 || _location.Length == 0 ? "" : "Creates " + Path.Combine(_location, _name);

    /// <summary>Opens the wizard with a free name and the engine checkout this editor runs from (when found).</summary>
    public void Open()
    {
        _enginePath = TemplateLocator.FindEngineCheckout() ?? _enginePath;
        _name = FreeName(_location, "MyGame");
        _log.Clear();
        _logText = "";
        LastResult = null;
        Visible = true;
        if (Sdk is null && _sdkCheck is null)
        {
            _sdkText = "Checking the .NET SDK…";
            _sdkCheck = DotnetSdk.DetectAsync();
        }

        Validate();
        if (EnsureLoaded() && Document.GetElementById("np-name") is { IsNull: false } field)
            field.Focus(focusVisible: true);
    }

    private static string FreeName(string location, string name)
    {
        if (!Directory.Exists(Path.Combine(location, name)))
            return name;
        for (var i = 2; ; i++)
            if (!Directory.Exists(Path.Combine(location, name + i)))
                return name + i;
    }

    private void SetSourceLater(EngineSource source)
    {
        if (source == EngineSource.Package && !PackageAvailable)
            return;
        _source = source;
        ValidateLater();
    }

    private void ValidateLater()
    {
        if (IsInsideTree)
            CallDeferred(static state => ((NewProjectDialog)state!).Validate(), this);
    }

    /// <summary>Validates the fields; the error line shows the first problem.</summary>
    public bool Validate()
    {
        var request = Request();
        var error = NewProjectValidation.ValidateName(request.Name)
                    ?? (Directory.Exists(request.ParentDirectory) || request.ParentDirectory.Length == 0
                        ? NewProjectValidation.ValidateLocation(request.ParentDirectory, request.Name)
                        : null)
                    ?? (_source == EngineSource.Checkout
                        ? NewProjectValidation.ValidateEnginePath(request.EnginePath)
                        : NewProjectValidation.ValidatePackageVersion(EngineInfo.Version))
                    ?? (Sdk is { IsSupported: false } sdk ? sdk.Error : null);
        _error = error ?? "";
        _model?.DirtyAll();
        return error is null;
    }

    /// <summary>Main thread, every frame while shown: the SDK check, creation output and completion.</summary>
    public void Tick()
    {
        if (_sdkCheck is { IsCompleted: true } check)
        {
            _sdkCheck = null;
            Sdk = check.IsCompletedSuccessfully ? check.Result : new DotnetSdkInfo(null, [], null, false, check.Exception?.GetBaseException().Message);
            _sdkText = Sdk.IsSupported ? $".NET SDK {Sdk.BestVersion}" : "";
            Validate();
        }

        var changed = false;
        while (_incoming.TryDequeue(out var line))
        {
            _log.Enqueue(line);
            while (_log.Count > MaxLogLines)
                _log.Dequeue();
            changed = true;
        }

        if (changed)
        {
            _logText = string.Join('\n', _log);
            _model?.Dirty("log");
        }

        if (_creation is { IsCompleted: true } creation)
        {
            _creation = null;
            _busy = false;
            _cancel?.Dispose();
            _cancel = null;
            var result = creation.IsCompletedSuccessfully
                ? creation.Result
                : new ProjectCreateResult(false, "", creation.Exception?.GetBaseException().Message ?? "Creating the project failed.", "", TimeSpan.Zero);
            LastResult = result;
            Finish(result);
        }
    }

    /// <summary>Starts creating the project (validated; SDK required).</summary>
    public bool Create()
    {
        if (_busy || !Validate())
            return false;
        if (Sdk is not { IsSupported: true, DotnetPath: { } dotnet })
        {
            _error = Sdk is null ? "Still checking the .NET SDK…" : Sdk.Error ?? "The .NET SDK was not found.";
            _model?.Dirty("error");
            return false;
        }

        // A checkout game uses that checkout's template; a package game the one shipped next to the editor (else, in a
        // development run, the checkout this editor runs from).
        var template = _source == EngineSource.Checkout
            ? TemplateLocator.FindTemplate(_enginePath.Trim())
            : TemplateLocator.FindTemplate(null) ?? TemplateLocator.FindTemplate(TemplateLocator.FindEngineCheckout());
        if (template is null)
        {
            _error = _source == EngineSource.Checkout
                ? $"The mfgame template was not found in {_enginePath} (Templates/MainframeEngine.Templates/content/mfgame)."
                : $"The mfgame template was not found next to the editor ({TemplateLocator.PackagedTemplateRelativePath}); reinstall the editor.";
            _model?.Dirty("error");
            return false;
        }

        Directory.CreateDirectory(_location.Trim());
        var request = Request();
        _busy = true;
        _error = "";
        _log.Clear();
        _cancel = new CancellationTokenSource();
        var creator = new ProjectCreator(dotnet, template);
        _incoming.Enqueue(_source == EngineSource.Package
            ? $"dotnet new mfgame -n {request.Name} --engine-source package --engine-version {EngineInfo.Version}"
            : $"dotnet new mfgame -n {request.Name} --engine-source project --engine-path {request.EnginePath}");
        _creation = creator.CreateAsync(request, line => _incoming.Enqueue(line), _cancel.Token);
        _model?.DirtyAll();
        Log.Info($"[Editor] Creating {request.ProjectDirectory}…");
        return true;
    }

    private NewProjectRequest Request() => new(_name.Trim(), _location.Trim(), _enginePath.Trim(), _source);

    private void Finish(ProjectCreateResult result)
    {
        if (!result.Succeeded)
        {
            _error = result.Error ?? "Creating the project failed.";
            Log.Error($"[Editor] {_error}");
            _model?.DirtyAll();
            return;
        }

        Log.Info($"[Editor] Created {result.ProjectDirectory} in {result.Duration.TotalSeconds:0.0} s.");
        Close();
        Workspace.Commands.OpenProject(result.ProjectDirectory);
    }

    /// <summary>Cancels a running creation, or closes the wizard.</summary>
    public void Cancel()
    {
        if (_busy)
        {
            _cancel?.Cancel();
            return;
        }

        Close();
    }

    private void Close() => HideAndReleaseFocus();

    private void BrowseLocation()
    {
        var start = Directory.Exists(_location) ? _location : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Workspace.FilePicker.Show(new FilePickerModel(FilePickerMode.Folder, start, []), "Project Location", "Choose", folder => Location = folder);
    }

    private void BrowseEngine()
    {
        var start = Directory.Exists(_enginePath) ? _enginePath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Workspace.FilePicker.Show(new FilePickerModel(FilePickerMode.Folder, start, []), "Engine Checkout", "Choose", folder => EnginePath = folder);
    }

    protected override void OnAttach(RmlDocument document) => document.AsElement().AddEventListener("keydown", e =>
    {
        if (!Visible)
            return;
        switch ((RmlKey)e.GetParameter("key_identifier", 0))
        {
            case RmlKey.Escape:
                Cancel();
                break;
            case RmlKey.Return or RmlKey.NumpadEnter:
                Create();
                break;
        }
    });
}
