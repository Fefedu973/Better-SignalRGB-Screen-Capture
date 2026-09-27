using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

public interface ISignalRgbEffectSettingsService
{
    SignalRgbEffectSettings Current { get; }
    event EventHandler<SignalRgbEffectSettings>? Changed;
    Task InitializeAsync();
    Task UpdateAsync(SignalRgbEffectSettings settings);
    Task ScheduleUpdateAsync(SignalRgbEffectSettings settings);
    Task FlushAsync();
}
