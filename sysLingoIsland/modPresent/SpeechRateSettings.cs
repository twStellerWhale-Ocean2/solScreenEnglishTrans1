namespace LingoIsland.Present;

/// <summary>
/// 播音速度偏好（v1.0.1，USR 回饋；session 內記憶、重啟重置——比照 <see cref="AutoPlaySettings"/>，持久化可後續增量）。
/// 由字典工具列之 Speed 下拉設定（百分比 50–200；100＝正常語速）；<see cref="SpeechService"/> 於每次 Speak 讀取套用，
/// 故 Play／單字發音／自動播放皆隨此速度（單一語音合成器＝全 App 一致）。
/// </summary>
public static class SpeechRateSettings
{
    /// <summary>播音速度百分比（50–150；100＝正常語速；下拉每 10% 一階）。</summary>
    public static int Percent { get; set; } = 100;

    /// <summary>
    /// 對映 SAPI <c>SpeechSynthesizer.Rate</c>（-10..+10；正＝快、負＝慢、0＝正常）：
    /// 100%→0、**每 10% 一級**（50%→-5、150%→+5），鉗制 [-10, 10]——與下拉 10% 階距一對一、均勻不跳。
    /// </summary>
    public static int SapiRate => System.Math.Clamp((int)System.Math.Round((Percent - 100) / 10.0), -10, 10);
}
