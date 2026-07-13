namespace LingoIsland.Present;

/// <summary>
/// 自動加入筆記偏好（session 內記憶，類比 <see cref="AutoPlaySettings"/>；Issue #34）：
/// 勾選後每次查詢成功即去重收藏至我的筆記。目前存於記憶體、重啟重置。
/// </summary>
public static class AutoAddSettings
{
    public static bool Enabled;
}
