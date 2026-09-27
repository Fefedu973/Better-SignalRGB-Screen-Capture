using System.Collections.Concurrent;

using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Services;

namespace BetterSignalRGB.RegressionTests;

internal static class EffectSettingsQueueTests
{
    public static async Task RunAsync()
    {
        var storage = new Storage();
        var service = new SignalRgbEffectSettingsService(storage);
        var first = service.ScheduleUpdateAsync(new() { Enabled = true, Hue = 10 });
        var second = service.ScheduleUpdateAsync(new() { Enabled = true, Hue = 20 });
        var third = service.ScheduleUpdateAsync(new() { Enabled = true, Hue = 30 });
        Assert.True(!third.IsCompleted && storage.Writes.IsEmpty, "Scheduled edits wait instead of writing on every slider event");
        await service.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await Task.WhenAll(first, second, third);
        Assert.Equal(1, storage.Writes.Count, "One debounce window persists only the newest complete snapshot");
        Assert.Equal(30, storage.Writes.Single().Hue, "Immediate shutdown flush preserves the latest slider value");
        Assert.Equal(30, service.Current.Hue, "Flush returns only after the value is published and persisted");

        var pending = service.ScheduleUpdateAsync(service.Current with { Hue = 40 });
        await service.UpdateAsync(service.Current with { Hue = 50 });
        await pending;
        Assert.Equal(2, storage.Writes.Count, "An immediate update supersedes an older waiting snapshot");
        Assert.Equal(50, service.Current.Hue, "An older debounce cannot overwrite a newer immediate update");

        var heldWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.HoldNextWrite = heldWrite.Task;
        storage.WriteStarted = writeStarted;
        var inFlight = service.UpdateAsync(service.Current with { Hue = 60 });
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var waiting = service.ScheduleUpdateAsync(service.Current with { Hue = 70 });
        var flushing = service.FlushAsync();
        var newest = service.ScheduleUpdateAsync(service.Current with { Hue = 80 });
        Assert.True(!flushing.IsCompleted, "Shutdown flush waits for a write already in progress");
        heldWrite.SetResult();
        await flushing.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.WhenAll(inFlight, waiting, newest);
        Assert.Equal(4, storage.Writes.Count, "Edits received during a write coalesce into one following write");
        Assert.Equal(80, service.Current.Hue, "Flush drains the newest edit received while an earlier write was in progress");

        storage.FailNextWrite = true;
        var rejected = service.ScheduleUpdateAsync(service.Current with { Hue = 90 });
        var failedFlush = service.FlushAsync();
        await ExpectWriteFailureAsync(rejected);
        await ExpectWriteFailureAsync(failedFlush);
        Assert.Equal(80, service.Current.Hue, "A failed scheduled write does not change confirmed appearance");
        var retry = service.ScheduleUpdateAsync(service.Current with { Hue = 100 });
        await service.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await retry;
        Assert.Equal(100, service.Current.Hue, "The queue recovers after a failed write");
        var count = storage.Writes.Count;
        await service.FlushAsync();
        Assert.Equal(count, storage.Writes.Count, "Flushing an idle queue performs no extra write");

        var automaticFirst = service.ScheduleUpdateAsync(service.Current with { Hue = 110 });
        var automaticLatest = service.ScheduleUpdateAsync(service.Current with { Hue = 120 });
        await Task.WhenAll(automaticFirst, automaticLatest).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(count + 1, storage.Writes.Count, "Normal debounce expiry persists one write without requiring a flush");
        Assert.Equal(120, service.Current.Hue, "Normal debounce expiry publishes the latest edit");
    }

    private static async Task ExpectWriteFailureAsync(Task write)
    {
        try { await write.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(false, "Write failure must reach each awaiting caller"); }
        catch (IOException) { Assert.True(true, "Write failure reaches each awaiting caller"); }
    }

    private sealed class Storage : ILocalSettingsService
    {
        public ConcurrentQueue<SignalRgbEffectSettings> Writes { get; } = new();
        public Task? HoldNextWrite;
        public TaskCompletionSource? WriteStarted;
        public bool FailNextWrite;

        public Task<T?> ReadSettingAsync<T>(string key) => Task.FromResult(default(T));

        public async Task SaveSettingAsync<T>(string key, T value)
        {
            if (HoldNextWrite is { } held)
            {
                HoldNextWrite = null;
                WriteStarted?.TrySetResult();
                await held;
            }
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("Simulated effect write failure");
            }
            Writes.Enqueue((SignalRgbEffectSettings)(object)value!);
        }
    }
}
