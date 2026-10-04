using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The New Project wizard (E4): name, parent folder and engine checkout, validated as you type, the .NET SDK check
/// (with the download link), then <c>dotnet new mfgame</c> through the <see cref="ProjectCreator"/> with live output;
/// the new project opens when it is done. Cancel stops a running creation.
/// </summary>
public sealed class NewProjectDialog : EditorDocument
{
    private const int MaxLogLines = 12;

    private readonly Queue<string> _log = new();
    private RmlDataModel? _model;
    private string _name = "MyGame";
    private string _location = DefaultLocation;
    private string _enginePath = "";
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
            .Bind("error", this, static d => d._error)
            .Bind("log", this, static d => d._logText)
            .Bind("sdk", this, static d => d._sdkText)
            .Bind("busy", this, static d => d._busy)
            .Bind("target", this, static d => d.TargetText())
            .Event("browse_location", _ => BrowseLocation())
            .Event("browse_engine", _ => BrowseEngine())
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

    private void ValidateLater()
    {
        if (IsInsideTree)
            CallDeferred(static state => ((NewProjectDialog)state!).Validate(), this);
    }

    /// <summary>Validates the fields; the error line shows the first problem.</summary>
    public bool Validate()
    {
        var request = new NewProjectRequest(_name.Trim(), _location.Trim(), _enginePath.Trim());
        var error = NewProjectValidation.ValidateName(request.Name)
                    ?? (Directory.Exists(request.ParentDirectory) || request.ParentDirectory.Length == 0
                        ? NewProjectValidation.ValidateLocation(request.ParentDirectory, request.Name)
                        : null)
                    ?? NewProjectValidation.ValidateEnginePath(request.EnginePath)
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

        var template = TemplateLocator.FindTemplate(_enginePath.Trim());
        if (template is null)
        {
            _error = $"The mfgame template was not found in {_enginePath} (Templates/MainframeEngine.Templates/content/mfgame).";
            _model?.Dirty("error");
            return false;
        }

        Directory.CreateDirectory(_location.Trim());
        var request = new NewProjectRequest(_name.Trim(), _location.Trim(), _enginePath.Trim());
        _busy = true;
        _error = "";
        _log.Clear();
        _cancel = new CancellationTokenSource();
        var creator = new ProjectCreator(dotnet, template);
        _incoming.Enqueue($"dotnet new mfgame -n {request.Name} --engine-path {request.EnginePath}");
        _creation = creator.CreateAsync(request, line => _incoming.Enqueue(line), _cancel.Token);
        _model?.DirtyAll();
        Log.Info($"[Editor] Creating {request.ProjectDirectory}…");
        return true;
    }

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
