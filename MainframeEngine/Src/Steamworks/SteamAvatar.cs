namespace MainframeEngine;

// TODO: Implement avatars in OpenGL
public static class SteamAvatar
{
	// public static Sprite GetAvatar(ulong steamId)
	// {
	// 	if (!Steam.Valid)
	// 		return null;
	// 		
	// 	var id = SteamFriends.GetLargeFriendAvatar((CSteamID)steamId);
	// 		
	// 	if (id != -1)
	// 	{
	// 		var texture2D = GetSteamImageAsTexture2D(id);
	// 		if (texture2D)
	// 			return Sprite.Create(texture2D, new Rect(0, 0, texture2D.width, texture2D.height), new Vector2(0.5f, 0.5f));
	// 	}
	//
	// 	Trace.TraceError($"Failed to load avatar for steamId: {steamId}");
	// 	return null;
	// }
	// 	
	// private static Texture? GetSteamImageAsTexture2D(int iImage)
	// {
	// 	var isValid = SteamUtils.GetImageSize(iImage, out var width, out var height);
	//
	// 	if (!isValid)
	// 		return null;
	// 		
	// 	var buffer = new byte[width * height * 4];
	// 	isValid = SteamUtils.GetImageRGBA(iImage, buffer, buffer.Length);
	// 		
	// 	if (!isValid)
	// 		return null;
	// 		
	// 	var ret = new Texture((int)width, (int)height, TextureFormat.RGBA32, false, false);
	// 	ret.LoadRawTextureData(buffer);
	// 	ret.Apply();
	//
	// 	return FlipTexture(ret);
	// }
	//
	// /// <summary>
	// /// Src: https://www.reddit.com/r/Unity3D/comments/5bp3pi/how_to_add_avatar_with_steamworks/
	// /// </summary>
	// /// <param name="original"></param>
	// /// <returns></returns>
	// private static Texture FlipTexture(Texture original)
	// {
	// 	var flipped = new Texture(original.width, original.height);
	//
	// 	var xN = original.width;
	// 	var yN = original.height;
	//
	// 	for (int i = 0; i < xN; i++)
	// 	for (int j = 0; j < yN; j++)
	// 		flipped.SetPixel(i, yN - j - 1, original.GetPixel(i, j));
	//
	// 	flipped.Apply();
	// 	return flipped;
	// }
}