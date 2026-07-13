namespace LingoIsland.Capture;

/// <summary>
/// 選區擷取結果：PNG 影像位元組＋像素尺寸。
/// 對應 design ＜III.B.(A)＞ [modCapture模組]→[modQuery模組] 之 ICaptureResult。
/// <see cref="IsPointMode"/>（Issue #54）＝true 時為**雙擊自動判斷模式**：影像為整螢幕、含游標處紅色標記，
/// 由查詢層以標記處為準辨識該句（非框選矩形），呈現層/查詢層據以選用不同提示。
/// </summary>
public sealed record CaptureResult(byte[] PngBytes, int Width, int Height, bool IsPointMode = false);
