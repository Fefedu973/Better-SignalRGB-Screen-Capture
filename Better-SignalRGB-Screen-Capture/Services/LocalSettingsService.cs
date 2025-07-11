﻿using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Core.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Core.Helpers;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;

using Microsoft.Extensions.Options;
using System.Diagnostics;

using Windows.ApplicationModel;
using Windows.Storage;

namespace Better_SignalRGB_Screen_Capture.Services;

public class LocalSettingsService : ILocalSettingsService
{
    private const string _defaultApplicationDataFolder = "Better-SignalRGB-Screen-Capture/ApplicationData";
    private const string _defaultLocalSettingsFile = "LocalSettings.json";

    private readonly IFileService _fileService;
    private readonly LocalSettingsOptions _options;

    private readonly string _localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private readonly string _applicationDataFolder;
    private readonly string _localsettingsFile;

    private IDictionary<string, object> _settings;

    private bool _isInitialized;

    public LocalSettingsService(IFileService fileService, IOptions<LocalSettingsOptions> options)
    {
        _fileService = fileService;
        _options = options.Value;

        _applicationDataFolder = Path.Combine(_localApplicationData, _options.ApplicationDataFolder ?? _defaultApplicationDataFolder);
        _localsettingsFile = _options.LocalSettingsFile ?? _defaultLocalSettingsFile;

        _settings = new Dictionary<string, object>();

        // Debug log the computed file paths
        Debug.WriteLine($"📁 LocalSettingsService initialized:");
        Debug.WriteLine($"   - IsRuntimeMSIX: {RuntimeHelper.IsMSIX}");
        if (RuntimeHelper.IsMSIX)
        {
            Debug.WriteLine($"   - Storage Method: MSIX ApplicationData.Current.LocalSettings");
        }
        else
        {
            Debug.WriteLine($"   - Storage Method: File-based");
            Debug.WriteLine($"   - Application Data Folder: {_applicationDataFolder}");
            Debug.WriteLine($"   - Settings File: {_localsettingsFile}");
            Debug.WriteLine($"   - Full Path: {Path.Combine(_applicationDataFolder, _localsettingsFile)}");
        }
    }

    private async Task InitializeAsync()
    {
        if (!_isInitialized)
        {
            Debug.WriteLine($"📖 Reading settings from: {Path.Combine(_applicationDataFolder, _localsettingsFile)}");
            _settings = await Task.Run(() => _fileService.Read<IDictionary<string, object>>(_applicationDataFolder, _localsettingsFile)) ?? new Dictionary<string, object>();

            _isInitialized = true;
        }
    }

    public async Task<T?> ReadSettingAsync<T>(string key)
    {
        if (RuntimeHelper.IsMSIX)
        {
            Debug.WriteLine($"📖 Reading setting '{key}' from MSIX ApplicationData");
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out var obj))
            {
                return await Json.ToObjectAsync<T>((string)obj);
            }
        }
        else
        {
            await InitializeAsync();

            if (_settings != null && _settings.TryGetValue(key, out var obj))
            {
                return await Json.ToObjectAsync<T>((string)obj);
            }
        }

        return default;
    }

    public async Task SaveSettingAsync<T>(string key, T value)
    {
        if (RuntimeHelper.IsMSIX)
        {
            Debug.WriteLine($"💾 Saving setting '{key}' to MSIX ApplicationData");
            ApplicationData.Current.LocalSettings.Values[key] = await Json.StringifyAsync(value);
        }
        else
        {
            await InitializeAsync();

            Debug.WriteLine($"💾 Saving setting '{key}' to: {Path.Combine(_applicationDataFolder, _localsettingsFile)}");
            _settings[key] = await Json.StringifyAsync(value);

            await Task.Run(() => _fileService.Save(_applicationDataFolder, _localsettingsFile, _settings));
        }
    }
}
