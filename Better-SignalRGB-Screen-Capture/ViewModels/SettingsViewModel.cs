using System.Reflection;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Helpers;
using Microsoft.Win32;

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

    private const string StartOnBootKey = "StartOnBoot";
    private const string BootInTrayKey = "BootInTray";
    private const string AutoRecordKey = "AutoStartRecordingOnBoot";
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
    private bool _autoStartRecordingOnBoot;

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

    partial void OnStartOnBootChanged(bool value) => SaveSettingAsync(StartOnBootKey, value);
    partial void OnBootInTrayChanged(bool value) => SaveSettingAsync(BootInTrayKey, value);
    partial void OnAutoStartRecordingOnBootChanged(bool value) => SaveSettingAsync(AutoRecordKey, value);
    partial void OnWaitForSourceAvailabilityChanged(bool value) => SaveSettingAsync(WaitForSourceAvailabilityKey, value);
    partial void OnStreamingPortChanged(int value) => SaveSettingAsync(StreamingPortKey, value);
    partial void OnHttpsPortChanged(int value) => SaveSettingAsync(HttpsPortKey, value);

    partial void OnElementThemeChanged(ElementTheme value)
    {
        _themeSelectorService.SetThemeAsync(value);
    }

    [RelayCommand]
    private void CheckForUpdate()
    {
        // TODO: Implement update check logic
    }

    private async void SaveSettingAsync(string key, object value)
    {
        await _localSettingsService.SaveSettingAsync(key, value);

        if (key == StartOnBootKey)
        {
            StartupHelper.SetStartOnBoot((bool)value);
        }
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
            var response = await client.GetAsync("https://api.github.com/repos/Fefedu973/Better-SignalRGB-Screen-Capture");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var repoInfo = JsonDocument.Parse(json).RootElement;
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
        StartOnBoot = await _localSettingsService.ReadSettingAsync<bool?>(StartOnBootKey) ?? false;
        BootInTray = await _localSettingsService.ReadSettingAsync<bool?>(BootInTrayKey) ?? false;
        AutoStartRecordingOnBoot = await _localSettingsService.ReadSettingAsync<bool?>(AutoRecordKey) ?? false;
        WaitForSourceAvailability = await _localSettingsService.ReadSettingAsync<bool?>(WaitForSourceAvailabilityKey) ?? true;
        StreamingPort = await _localSettingsService.ReadSettingAsync<int?>(StreamingPortKey) ?? 8080;
        HttpsPort = await _localSettingsService.ReadSettingAsync<int?>(HttpsPortKey) ?? 8443;
    }

    private async Task LoadContributorsAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Better-SignalRGB-Screen-Capture");
            var response = await client.GetAsync("https://api.github.com/repos/Fefedu973/Better-SignalRGB-Screen-Capture/contributors");
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
