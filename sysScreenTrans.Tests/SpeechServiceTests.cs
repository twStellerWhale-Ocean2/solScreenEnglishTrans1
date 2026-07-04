using ScreenTrans.Present;
using Xunit;

namespace ScreenTrans.Tests;

/// <summary>
/// Windows 語音服務（[techItem語音合成]，Issue #9）：語音列舉與缺失語音之容錯。
/// 不實際發聲（介面攔截精神）；僅驗建構與列舉不丟例外。
/// </summary>
public class SpeechServiceTests
{
    [Fact]
    public void InstalledVoiceNames_DoesNotThrow_ReturnsNonNull()
    {
        var voices = SpeechService.InstalledVoiceNames();
        Assert.NotNull(voices); // 空清單亦可（無安裝語音），但不得為 null 或丟例外
    }

    [Fact]
    public void Ctor_WithMissingVoiceName_FallsBack_DoesNotThrow()
    {
        // 指定不存在的語音 → 應吞例外退回系統預設、不當機（契約：語音缺失不當機）
        using var svc = new SpeechService("NoSuchVoice-xyz-9999");
        Assert.NotNull(svc);
    }

    [Fact]
    public void Ctor_WithNullVoice_UsesDefault_DoesNotThrow()
    {
        using var svc = new SpeechService(null);
        Assert.NotNull(svc);
    }
}
