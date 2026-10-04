using Steamworks;

namespace MainframeEngine;

public class SteamAchievements
{
    public static bool IsUnlocked(string id)
    {
        return Steam.Valid && SteamUserStats.GetAchievement(id, out var isAchieved) && isAchieved;
    }

    public static bool Unlock(string id)
    {
        return Steam.Valid && SteamUserStats.SetAchievement(id);
    }

    public static bool Clear(string id)
    {
        return Steam.Valid && SteamUserStats.ClearAchievement(id);
    }
}