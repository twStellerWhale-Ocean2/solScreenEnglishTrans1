namespace LingoIsland.Present;

/// <summary>
/// 發音回饋通知抽象（[modPresent模組] 發音回饋通知契約，spec#10／#101）：將發音練習之評分結果與失敗態
/// 以**系統通知**呈現、進通知中心可回看；未安裝／dev 裸跑／WinRT 送出失敗時由實作**降級為應用內浮層**、不崩潰。
/// 介面化使單元測試可注入假實作（不實際彈通知）。
/// </summary>
public interface INotificationService
{
    /// <summary>顯示一則通知（<paramref name="title"/> 標題＋<paramref name="body"/> 內文；內文可含 <c>\n</c> 多行）。</summary>
    void Show(string title, string body);
}
