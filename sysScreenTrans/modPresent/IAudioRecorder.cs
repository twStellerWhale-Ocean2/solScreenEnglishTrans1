namespace ScreenTrans.Present;

/// <summary>
/// 錄音開始結果（[modPresent模組] 麥克風錄音契約，spec#10）：區分「無擷取裝置」與「OS 隱私未授權」，
/// 供 UI 給各自明確之降級訊息與下一步。
/// </summary>
public enum RecordStart
{
    /// <summary>已開始錄音。</summary>
    Ok,
    /// <summary>系統無可用麥克風裝置。</summary>
    NoDevice,
    /// <summary>裝置存在但被 OS 隱私設定封鎖（Windows 隱私權→麥克風）。</summary>
    PermissionDenied,
    /// <summary>其他未預期失敗。</summary>
    Failed,
}

/// <summary>
/// 麥克風錄音抽象（[modPresent模組] 麥克風錄音契約，spec#10）——介面化使單元測試可注入假錄音、
/// 不實際佔用麥克風；發音練習「按住錄音／放開停止」藉此擷取語音。
/// </summary>
public interface IAudioRecorder
{
    /// <summary>目前是否錄音中。</summary>
    bool IsRecording { get; }

    /// <summary>
    /// 錄音期間即時音量回報（0–1，spec#10 成績框藍色音量條）：每個擷取緩衝算出一次、由背景執行緒引發，
    /// 訂閱端須自行 marshal 回 UI 執行緒更新。非錄音態不引發。
    /// </summary>
    event Action<double>? LevelChanged;

    /// <summary>開始擷取；回 <see cref="RecordStart.Ok"/> 或無法開始之原因。</summary>
    RecordStart Start();

    /// <summary>
    /// 停止並回傳 WAV bytes；太短（低於最短時長）或無資料回 <c>null</c>，並以
    /// <paramref name="tooShort"/> 標示是否因太短（供 UI「錄音太短」提示）。
    /// </summary>
    byte[]? Stop(out bool tooShort);
}
