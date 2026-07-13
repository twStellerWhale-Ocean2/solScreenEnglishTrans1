namespace LingoIsland.Present;

/// <summary>
/// 自動播放偏好（session 內記憶；勾選後框選完即自動朗讀對應語言）。
/// 目前存於記憶體、重啟重置；持久化到 appsettings 可後續增量。
/// </summary>
public static class AutoPlaySettings
{
    public static bool English;
    public static bool Chinese;
}
