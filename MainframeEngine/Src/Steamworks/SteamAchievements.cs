namespace MainframeEngine;

/// <summary>
/// Steam achievements by API name. <see cref="Unlock"/> and <see cref="Clear"/> persist immediately
/// (<c>StoreStats</c>). All calls return false without Steam.
/// </summary>
public static class SteamAchievements
{
    public static bool IsUnlocked(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Steam.Valid && SteamApi.IsAchievementUnlocked(id);
    }

    /// <summary>Unlocks and stores the achievement (Steam shows the notification once stored).</summary>
    /// <returns>True if Steam accepted the unlock (storing is reported separately in the log).</returns>
    public static bool Unlock(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!Steam.Valid || !SteamApi.SetAchievement(id))
            return false;
        StoreStats();
        return true;
    }

    /// <summary>Re-locks and stores the achievement (testing).</summary>
    public static bool Clear(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!Steam.Valid || !SteamApi.ClearAchievement(id))
            return false;
        StoreStats();
        return true;
    }

    /// <summary>Uploads pending stat and achievement changes. Called by <see cref="Unlock"/> and <see cref="Clear"/>.</summary>
    public static bool StoreStats()
    {
        if (!Steam.Valid)
            return false;
        if (SteamApi.StoreStats())
            return true;
        Log.Warning("[Steam] StoreStats failed; achievement changes are not persisted yet");
        return false;
    }
}
