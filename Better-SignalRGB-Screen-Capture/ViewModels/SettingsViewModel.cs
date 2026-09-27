using System.Reflection;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Helpers;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Xaml;

using Windows.ApplicationModel;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class SettingsViewModel : ObservableRecipient
{
    private readonly IThemeSelectorService _themeSelectorService;
    private readonly ILocalSettingsService _localSettingsService;

    private bool _loadingSettings = true;
    private readonly SemaphoreSlim _startupGate = new(1, 1);
    private bool _updatingStartupState;
    private bool _confirmedStartupState;
    private int _startupChangeVersion;

    private const string StartOnBootKey = "StartOnBoot";
    private const string BootInTrayKey = "BootInTray";
    private const string AutoRecordKey = StartupPreferences.AutoStartRecordingKey;
    private const string WaitForSourceAvailabilityKey = "WaitForSourceAvailability";
    private const string StreamingPortKey = "StreamingPort";
    private const string HttpsPortKey = "HttpsPort";

    [ObservableProperty]
    private ElementTheme _elementTheme;

    public IReadOnlyList<ElementTheme> Themes { get; } = 
        Enum.GetValues(typeof(ElementTheme)).Cast<ElementTheme>().ToList();

    [ObservableProperty]
    private string _versionDescription;

    [ObservableProperty]
    private bool _startOnBoot;

    [ObservableProperty]
    private bool _bootInTray;

    [ObservableProperty]
    private bool _autoStartRecordingOnBoot = StartupPreferences.AutoStartRecordingByDefault;

    [ObservableProperty]
    private bool _waitForSourceAvailability;

    [ObservableProperty]
    private int _streamingPort = 8080;

    [ObservableProperty]
    private int _httpsPort = 8443;

    [ObservableProperty]
    private string _authorAvatar = "https://avatars.githubusercontent.com/Fefedu973";

    [ObservableProperty]
    private int _starCount;

    public ObservableCollection<GitHubContributor> Contributors { get; } = new();

    partial void OnStartOnBootChanged(bool value)
    {
        if (!_loadingSettings && !_updatingStartupState) _ = UpdateStartupAsync(value);
    }
    partial void OnBootInTrayChanged(bool value) => SaveSettingAsync(BootInTrayKey, value);
    partial void OnAutoStartRecordingOnBootChanged(bool value) => SaveSettingAsync(AutoRecordKey, value);
    partial void OnWaitForSourceAvailabilityChanged(bool value)
    {
        if (!_loadingSettings) App.GetService<MainViewModel>().WaitForSourceAvailability = value;
    }
    partial void OnStreamingPortChanged(int value) => SaveSettingAsync(StreamingPortKey, value);
    partial void OnHttpsPortChanged(int value) => SaveSettingAsync(HttpsPortKey, value);

    partial void OnElementThemeChanged(ElementTheme value)
    {
        _themeSelectorService.SetThemeAsync(value);
    }

    [RelayCommand]
    private async Task ViewReleasesAsync()
    {
        try
        {
            var opened = await Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://github.com/Fefedu973/Better-SignalRGB-Screen-Capture/releases"));
            if (!opened) App.GetService<MainViewModel>().StatusMessage = "Could not open the release page in your browser.";
        }
        catch (Exception ex)
        {
            App.GetService<MainViewModel>().StatusMessage = $"Could not open the release page: {ex.Message}";
        }
    }

    private async void SaveSettingAsync(string key, object value)
    {
        if (_loadingSettings) return;
        try
        {
            if (key is StreamingPortKey or HttpsPortKey && (int)value is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(value), "Ports must be between 1 and 65535.");
            await _localSettingsService.SaveSettingAsync(key, value);
        }
        catch (Exception ex) { App.GetService<MainViewModel>().StatusMessage = $"Could not save setting: {ex.Message}"; }
    }

    private async Task UpdateStartupAsync(bool requested)
    {
        var version = ++_startupChangeVersion;
        await _startupGate.WaitAsync();
        try
        {
            if (version != _startupChangeVersion) return;
            _confirmedStartupState = await StartupHelper.SetStartOnBootAsync(requested);
            await _localSettingsService.SaveSettingAsync(StartOnBootKey, _confirmedStartupState);
            if (version == _startupChangeVersion && requested != _confirmedStartupState)
                App.GetService<MainViewModel>().StatusMessage = requested
                    ? "Windows did not enable automatic startup. Check Startup apps in Windows Settings."
                    : "Windows keeps automatic startup enabled by policy.";
        }
        catch (Exception ex)
        {
            // If a write partially succeeded, reconcile against the OS before updating the toggle.
            try
            {
                _confirmedStartupState = await StartupHelper.IsRegisteredAsync();
                await _localSettingsService.SaveSettingAsync(StartOnBootKey, _confirmedStartupState);
            }
            catch { /* Preserve the last confirmed state when Windows cannot be queried. */ }
            App.GetService<MainViewModel>().StatusMessage = $"Could not change automatic startup: {ex.Message}";
        }
        finally
        {
            if (version == _startupChangeVersion) SetConfirmedStartupState();
            _startupGate.Release();
        }
    }

    private void SetConfirmedStartupState()
    {
        _updatingStartupState = true;
        try { StartOnBoot = _confirmedStartupState; }
        finally { _updatingStartupState = false; }
    }

    public SettingsViewModel(IThemeSelectorService themeSelectorService, ILocalSettingsService localSettingsService)
    {
        _themeSelectorService = themeSelectorService;
        _localSettingsService = localSettingsService;
        _elementTheme = _themeSelectorService.Theme;
        _versionDescription = GetVersionDescription();

        // Load persisted values
        _ = LoadSettingsAsync();
        _ = LoadGitHubStatsAsync();
        _ = LoadContributorsAsync();
    }

    private static string GetVersionDescription()
    {
        Version version;

        if (RuntimeHelper.IsMSIX)
        {
            var packageVersion = Package.Current.Id.Version;

            version = new(packageVersion.Major, packageVersion.Minor, packageVersion.Build, packageVersion.Revision);
        }
        else
        {
            version = Assembly.GetExecutingAssembly().GetName().Version!;
        }

        return $"{"AppDisplayName".GetLocalized()} - {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

    private async Task LoadGitHubStatsAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Better-SignalRGB-Screen-Capture");
            using var response = await client.GetAsync("https://api.github.com/repos/Fefedu973/Better-SignalRGB-Screen-Capture");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(json);
                var repoInfo = document.RootElement;
                if (repoInfo.TryGetProperty("stargazers_count", out var stars))
                {
                    StarCount = stars.GetInt32();
                }
            }
        }
        catch { /* Silently fail, we'll just show 0 stars */ }
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            try
            {
                _confirmedStartupState = await StartupHelper.IsRegisteredAsync();
                SetConfirmedStartupState();
            }
            catch (Exception ex) { App.GetService<MainViewModel>().StatusMessage = $"Could not read automatic startup state: {ex.Message}"; }
            BootInTray = await _localSettingsService.ReadSettingAsync<bool?>(BootInTrayKey) ?? false;
            AutoStartRecordingOnBoot = await StartupPreferences.ReadAutoStartRecordingAsync(_localSettingsService);
            WaitForSourceAvailability = await _localSettingsService.ReadSettingAsync<bool?>(WaitForSourceAvailabilityKey) ?? true;
            StreamingPort = await _localSettingsService.ReadSettingAsync<int?>(StreamingPortKey) ?? 8080;
            HttpsPort = await _localSettingsService.ReadSettingAsync<int?>(HttpsPortKey) ?? 8443;
        }
        catch (Exception ex) { App.GetService<MainViewModel>().StatusMessage = $"Could not load settings: {ex.Message}"; }
        finally { _loadingSettings = false; }
    }

    private async Task LoadContributorsAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Better-SignalRGB-Screen-Capture");
            using var response = await client.GetAsync("https://api.github.com/repos/Fefedu973/Better-SignalRGB-Screen-Capture/contributors");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var contributors = JsonSerializer.Deserialize<List<GitHubContributor>>(json);
            if (contributors != null)
            {
                foreach (var contributor in contributors)
                {
                    Contributors.Add(contributor);
                }
            }
        }
        catch (Exception)
        {
            // Silently fail, as this is not critical
        }
    }
}
