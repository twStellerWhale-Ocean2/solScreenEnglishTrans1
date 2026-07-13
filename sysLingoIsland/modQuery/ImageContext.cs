namespace LingoIsland.Query;

/// <summary>
/// 圖片情境解釋結果（Issue #36／#53，spec#9）：<see cref="Name"/>＝可明確辨識之具名作品
/// （遊戲／影集／電影／應用名，無法辨識回空字串、不臆測）、<see cref="Description"/>＝一兩句繁中情境描述。
/// 供情境分頁「以圖片自動解釋」一次取得名稱與描述（名稱僅在情境名未填時自動填入，#53）。
/// </summary>
public sealed record ImageContext(string Name, string Description);
