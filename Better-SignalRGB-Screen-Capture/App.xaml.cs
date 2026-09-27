using Better_SignalRGB_Screen_Capture.Activation;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Core.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Core.Services;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Notifications;
using Better_SignalRGB_Screen_Capture.Services;
using Better_SignalRGB_Screen_Capture.Services.NativeOutput;
using Better_SignalRGB_Screen_Capture.ViewModels;
using Better_SignalRGB_Screen_Capture.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;

namespace Better_SignalRGB_Screen_Capture;

// To learn more about WinUI 3, see https://docs.microsoft.com/windows/apps/winui/winui3/.
public partial class App : Application
{
    // The .NET Generic Host provides dependency injection, configuration, logging, and other services.
    // https://docs.microsoft.com/dotnet/core/extensions/generic-host
    // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
    // https://docs.microsoft.com/dotnet/core/extensions/configuration
    // https://docs.microsoft.com/dotnet/core/extensions/logging
    public IHost Host
    {
        get;
    }

    public static T GetService<T>()
        where T : class
    {
        if ((App.Current as App)!.Host.Services.GetService(typeof(T)) is not T service)
        {
            throw new ArgumentException($"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs.");
        }

        return service;
    }

    public static WindowEx MainWindow { get; } = new MainWindow();

    public static UIElement? AppTitlebar { get; set; }

    public bool IsShuttingDown { get; private set; }
    public event EventHandler? ShuttingDown;

    /// <summary>Release UI-owned native resources before disposing application services.</summary>
    public void PrepareForShutdown()
    {
        if (IsShuttingDown) return;
        IsShuttingDown = true;
        // Tray exit calls this on the UI thread. Actual window destruction is a
        // second idempotent path; a cancelled close/minimize does not raise Closed.
        var handlers = ShuttingDown;
        ShuttingDown = null;
        if (handlers == null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception) { ApplicationErrorLog.Write("Shutdown: release UI resources", exception); }
        }
    }

    public App()
    {
        ApplicationErrorLog.Initialize();
        UnhandledException += App_UnhandledException;
        ApplicationErrorLog.Write("App constructor: initialize XAML");
        InitializeComponent();

        Host = Microsoft.Extensions.Hosting.Host.
        CreateDefaultBuilder().
        UseContentRoot(AppContext.BaseDirectory).
        ConfigureServices((context, services) =>
        {
            // Default Activation Handler
            services.AddTransient<ActivationHandler<LaunchActivatedEventArgs>, DefaultActivationHandler>();

            // Other Activation Handlers
            services.AddTransient<IActivationHandler, AppNotificationActivationHandler>();

            // Services
            services.AddSingleton<IAppNotificationService, AppNotificationService>();
            services.AddSingleton<ILocalSettingsService, LocalSettingsService>();
            services.AddSingleton<IThemeSelectorService, ThemeSelectorService>();
            services.AddTransient<IWebViewService, WebViewService>();
            services.AddTransient<INavigationViewService, NavigationViewService>();

            services.AddSingleton<IActivationService, ActivationService>();
            services.AddSingleton<IPageService, PageService>();
            services.AddSingleton<INavigationService, NavigationService>();

            // Capture and streaming services
            services.AddSingleton<IWebsiteCaptureHostFactory, WebsiteCaptureHostFactory>();
            services.AddSingleton<ISignalRgbEffectSettingsService, SignalRgbEffectSettingsService>();
            services.AddSingleton<ISceneLibraryStorage, SceneLibraryStorage>();
            services.AddSingleton<ISceneLibraryService, SceneLibraryService>();
            services.AddSingleton<ISceneFilePickerService, SceneFilePickerService>();
            services.AddSingleton<ISignalRgbConnectionService, SignalRgbConnectionService>();
            services.AddSingleton<IPipelineDiagnosticsService, PipelineDiagnosticsService>();
            services.AddSingleton<ICaptureService, CaptureService>();
            services.AddSingleton<IMjpegStreamingService, MjpegStreamingService>();
            services.AddSingleton<IKestrelApiService, KestrelApiService>();
            services.AddSingleton<ICompositeFrameService, CompositeFrameService>();
            services.AddSingleton<INativeControlDispatcher>(_ => new NativeControlDispatcher(
                action => MainWindow.DispatcherQueue.TryEnqueue(() => action()), () => MainWindow.DispatcherQueue.HasThreadAccess));
            services.AddSingleton<INativeSceneController>(provider => new NativeSceneController(
                () => provider.GetRequiredService<MainViewModel>(), provider.GetRequiredService<INativeControlDispatcher>()));
            services.AddSingleton<NativeControlService>();
            services.AddSingleton<NativeOutputService>(provider => new NativeOutputService(
                (CompositeFrameService)provider.GetRequiredService<ICompositeFrameService>(), provider.GetRequiredService<NativeControlService>(),
                token => StreamingCanvasSnapshot.CaptureOnUiAsync(vm =>
                {
                    var control = provider.GetRequiredService<NativeControlService>().Current;
                    return new CompositeRenderSnapshot(vm.Sources.Select((source, index) =>
                        StreamingSourceSnapshot.FromSource(source, vm.Sources.Count - index - 1)).Reverse().ToArray(),
                        new NativeCompositionContext(vm.NativeState, control.Revision, control.EffectiveSettings));
                }, token)));
            services.AddSingleton<NativeApiServer>();
            services.AddSingleton<NativeIntegrationService>(provider => new NativeIntegrationService(
                provider.GetRequiredService<ILocalSettingsService>(), provider.GetRequiredService<NativeOutputService>(),
                provider.GetRequiredService<NativeApiServer>(), provider.GetRequiredService<NativeControlService>(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalSettingsOptions>>().Value.ApplicationDataFolder
                        ?? "Better-SignalRGB-Screen-Capture/ApplicationData")));

            // Core Services
            services.AddSingleton<ISampleDataService, SampleDataService>();
            services.AddSingleton<IFileService, FileService>();

            // Views and ViewModels
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<SettingsPage>();
            services.AddTransient<OutputViewModel>();
            services.AddTransient<OutputPage>();
            services.AddTransient<DataGridViewModel>();
            services.AddTransient<ContentGridDetailViewModel>();
            services.AddTransient<ContentGridViewModel>();
            services.AddTransient<ListDetailsViewModel>();
            services.AddTransient<WebViewViewModel>();
            services.AddTransient<WebViewPage>();
            services.AddSingleton<MainViewModel>();
            services.AddTransient<MainPage>();
            services.AddTransient<ShellPage>();
            services.AddTransient<ShellViewModel>();

            // Tray icon
            services.AddSingleton<TrayIconService>();

            // Configuration
            services.Configure<LocalSettingsOptions>(context.Configuration.GetSection(nameof(LocalSettingsOptions)));
        }).
        Build();

        ApplicationErrorLog.Configure(Host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalSettingsOptions>>().Value.ApplicationDataFolder);
        ApplicationErrorLog.Write("App constructor: initialize notifications");

        App.GetService<IAppNotificationService>().Initialize();
        MainWindow.Closed += (_, _) => PrepareForShutdown();
        ApplicationErrorLog.Write("App constructor complete");
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        ApplicationErrorLog.Write("XAML.UnhandledException", e.Exception, e.Message);
    }

    protected async override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            base.OnLaunched(args);
            await LaunchAsync(args);
        }
        catch (Exception exception)
        {
            // DispatcherQueue may rethrow async-void failures as native stowed exceptions,
            // bypassing XAML.UnhandledException. Record the original managed stack first.
            ApplicationErrorLog.Write("Launch failed", exception);
            throw;
        }
    }

    private async Task LaunchAsync(LaunchActivatedEventArgs args)
    {
        ApplicationErrorLog.Write("Launch: activate application");
        await App.GetService<IActivationService>().ActivateAsync(args);

        // Initialize tray icon service AFTER MainWindow is fully activated
        ApplicationErrorLog.Write("Launch: initialize tray icon");
        _ = GetService<TrayIconService>();

        // If user prefers to start in tray, hide the main window after activation
        var localSettings = GetService<ILocalSettingsService>();
        var startInTray = await localSettings.ReadSettingAsync<bool?>("BootInTray");
        if (startInTray == true)
        {
            App.MainWindow.Hide();
        }

        var autoRecord = await StartupPreferences.ReadAutoStartRecordingAsync(localSettings);
        if (autoRecord)
        {
            var vm = GetService<MainViewModel>();
            try
            {
                await vm.Initialization;
                if (!vm.IsRecording) await vm.ToggleRecordingCommand.ExecuteAsync(null);
            }
            catch (Exception exception)
            {
                ApplicationErrorLog.Write("Launch: automatic recording", exception);
                vm.StatusMessage = $"Could not start recording automatically: {exception.Message}";
            }
        }
        await GetService<NativeIntegrationService>().InitializeAsync();
        ApplicationErrorLog.Write("Launch complete");
    }
}
