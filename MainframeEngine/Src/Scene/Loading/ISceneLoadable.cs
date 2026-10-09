namespace MainframeEngine;

/// <summary>
/// A node with heavy set-up work that takes part in an asynchronous scene change (<see cref="SceneTree.ChangeSceneToFileAsync"/>,
/// ADR 0183) instead of blocking <see cref="Node.OnReady"/>, so the main loop — window events, audio and the loading
/// screen — keeps running while it loads, and the loading screen shows its progress.
/// </summary>
/// <remarks>
/// <para>
/// Both members are optional. The synchronous path (<see cref="SceneTree.ChangeSceneToFile"/>, the editor, tests) never
/// calls them, so a loadable node must still do its work in <see cref="Node.OnReady"/> when
/// <see cref="LoadInBackground"/> has not run.
/// </para>
/// <para>
/// <see cref="LoadInBackground"/> runs on a worker thread while the scene is still outside the tree: build what
/// <see cref="Node.OnReady"/> would (child nodes, meshes, resources, CPU data). Nodes constructed there are not in any tree
/// yet, so that is safe; never touch the tree, the servers, Vulkan, RmlUi, physics or audio from it. The tree's own work
/// (GPU resources, bodies) happens when the scene enters it on the main thread.
/// </para>
/// </remarks>
public interface ISceneLoadable
{
    /// <summary>This node's share of the loading screen's progress bar relative to the scene's other loadables (default 1).</summary>
    float LoadWeight => 1f;

    /// <summary>
    /// Worker thread, scene outside the tree (asynchronous loads only): do the heavy work now, reporting through
    /// <paramref name="progress"/> (fraction and stage label) and checking its cancellation token between steps.
    /// An exception fails the load.
    /// </summary>
    void LoadInBackground(SceneLoadProgress progress)
    {
    }

    /// <summary>
    /// Main thread, once per frame after the scene entered the tree (asynchronous loads only), until it returns true:
    /// work that needs the tree and several frames (GPU warm-up), reported through <paramref name="progress"/>. The
    /// loading screen stays up until every loadable of the scene returns true.
    /// </summary>
    bool PollLoaded(SceneLoadProgress progress) => true;
}
