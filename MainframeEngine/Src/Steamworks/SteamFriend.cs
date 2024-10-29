namespace MainframeEngine;

public readonly struct SteamFriend(ulong steamId, string username) : IEquatable<SteamFriend>, IComparable<SteamFriend>
{
	public readonly ulong SteamId = steamId;
	public readonly string Username = username;
		
	public bool IsMe => SteamId == Steam.SteamId;

	public override string ToString()
	{
		return $"Username: {Username}, SteamId: {SteamId}";
	}

	public bool Equals(SteamFriend other)
	{
		return SteamId == other.SteamId;
	}

	public override bool Equals(object? obj)
	{
		return obj is SteamFriend other && Equals(other);
	}

	public override int GetHashCode()
	{
		return SteamId.GetHashCode();
	}

	public int CompareTo(SteamFriend other)
	{
		return SteamId.CompareTo(other.SteamId);
	}

	// TODO: Implement Getting avatar
	// public Sprite GetAvatarSprite()
	// {
	// 	var sprite = SteamAvatar.GetAvatar(SteamId);
	// 	if (!sprite)
	// 		Trace.TraceWarning($"Failed to get avatar for: {ToString()}");
	// 	return sprite;
	// }

    public static bool operator ==(SteamFriend left, SteamFriend right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(SteamFriend left, SteamFriend right)
    {
        return !(left == right);
    }
}