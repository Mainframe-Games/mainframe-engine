namespace MainframeEngine;

/// <summary>
/// Runs the <see cref="Steam"/> service inside the engine's server lifecycle: initializes it when registered,
/// pumps <see cref="Steam.RunCallbacks"/> once per frame after the scene tree's process step, and shuts it down
/// with the other servers. <see cref="Engine"/> registers one when <see cref="EngineOptions.SteamAppId"/> is set.
/// Steam stays optional: when it cannot start, the server is inert.
/// </summary>
public sealed class SteamServer : IFrameServer
{
    public SteamServer(uint appId, bool writeDevAppIdFile = false)
    {
        Started = Steam.TryInitialize(appId, writeDevAppIdFile);
        if (!Started)
            Log.Info($"[Steam] Not running: {Steam.StatusMessage}");
    }

    /// <summary>True if Steam initialized.</summary>
    public bool Started { get; }

    public void Process(in GameTime gameTime)
    {
        if (Started)
            Steam.RunCallbacks();
    }

    public void Dispose()
    {
        if (Started)
            Steam.Shutdown();
    }
}
