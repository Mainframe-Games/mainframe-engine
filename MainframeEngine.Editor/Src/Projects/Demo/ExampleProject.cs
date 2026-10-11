namespace MainframeEngine.Editor;

/// <summary>
/// A downloadable example project, zipped per release by <c>build/package-example.sh</c>: the Demo (one scene per feature)
/// or the Forest showcase. <see cref="AssetName"/> is both the release asset's name and the folder it is saved to.
/// </summary>
public sealed record ExampleProject(string Id, string Title, string Icon)
{
    public static ExampleProject Demo { get; } = new("Demo", "Mainframe Demo", "device-gamepad-2");

    public static ExampleProject Forest { get; } = new("Forest", "Forest Demo", "mountain");

    /// <summary>The examples in the Project Manager's download menu, in order.</summary>
    public static IReadOnlyList<ExampleProject> All { get; } = [Demo, Forest];

    /// <summary><c>MainframeEngine.Demo</c>: the release asset (<c>MainframeEngine.Demo-v1.2.3.zip</c>) and the saved folder.</summary>
    public string AssetName => "MainframeEngine." + Id;

    public static ExampleProject? Find(string id) => All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
}
