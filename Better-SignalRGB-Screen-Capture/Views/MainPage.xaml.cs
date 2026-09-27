using System.Collections.Specialized;
using Better_SignalRGB_Screen_Capture.Contracts.ViewModels;
using Better_SignalRGB_Screen_Capture.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class MainPage : Page, INavigationAware
{
    // WinUI rejects ZIndex values above 1,000,000, including int.MaxValue.
    private const int GroupOverlayZIndex = 999_998;
    private const int SelectionOverlayZIndex = 999_999;
    private const int GuideOverlayZIndex = 1_000_000;
    public MainViewModel ViewModel { get; }
    private enum BackgroundGestureKind { Pan, Select }
    private sealed record BackgroundGesture(uint PointerId, BackgroundGestureKind Kind, Point Start,
        double ScrollX = 0, double ScrollY = 0, bool AddToSelection = false);
    private BackgroundGesture? _backgroundGesture;
    private GroupSelectionControl? _groupSelectionControl;
    private bool _viewModelSubscribed;
    private bool _canvasUpdateQueued;
    private bool _selectionUpdateQueued;
    private bool _isReorderingSources;

    public MainPage()
    {
        ViewModel = App.GetService<MainViewModel>();
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        UpdateFlipIconsTheme();

        Loaded += async (sender, e) => await ExecuteUiOperationAsync("load the canvas", async () =>
        {
            SubscribeViewModel();
            await ViewModel.Initialization;
            if (!IsLoaded) return;
            UpdateCanvas();
        });
        Unloaded += (_, _) => UnsubscribeViewModel();
        SourceCanvas.PointerReleased += MainPage_PointerReleased_ForSelection;
        SourceCanvas.PointerCanceled += CancelCanvasInteraction;
        SourceCanvas.PointerCaptureLost += CancelCanvasInteraction;

        this.ActualThemeChanged += (s,e) => UpdateFlipIconsTheme();
    }

    private void SubscribeViewModel()
    {
        if (_viewModelSubscribed) return;
        _viewModelSubscribed = true;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.CanvasInteractionCancellationRequested += OnCanvasInteractionCancellationRequested;
        ViewModel.Sources.CollectionChanged += OnSourcesCollectionChanged;
        ViewModel.SelectedSources.CollectionChanged += OnViewModelSelectionChanged;
        ViewModel.SourcesMoved += OnSourcesMoved;
        ViewModel.GroupSelectionBoundsChanged += OnGroupSelectionBoundsChangedFromViewModel;
    }

    private void UnsubscribeViewModel()
    {
        ClearSnapGuides();
        if (!_viewModelSubscribed) return;
        _viewModelSubscribed = false;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.CanvasInteractionCancellationRequested -= OnCanvasInteractionCancellationRequested;
        ViewModel.Sources.CollectionChanged -= OnSourcesCollectionChanged;
        ViewModel.SelectedSources.CollectionChanged -= OnViewModelSelectionChanged;
        ViewModel.SourcesMoved -= OnSourcesMoved;
        ViewModel.GroupSelectionBoundsChanged -= OnGroupSelectionBoundsChangedFromViewModel;
    }

    private void OnCanvasInteractionCancellationRequested(object? sender, EventArgs args)
    {
        _groupSelectionControl?.TryCancelInteraction();
        foreach (var control in SourceCanvas.Children.OfType<DraggableSourceItem>()) control.TryCancelInteraction();
        EndCanvasInteraction();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsSnappingEnabled)) ClearSnapGuides();
        if (e.PropertyName == nameof(ViewModel.SelectedSourcesLocked)) UpdateSelectionOnCanvas();
        if (e.PropertyName == nameof(ViewModel.IsPreviewing))
        {
            UpdatePreviewCanvas();
        }
    }

    private void OnSourcesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_canvasUpdateQueued)
        {
            _canvasUpdateQueued = true;
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                _canvasUpdateQueued = false;
                if (IsLoaded) ExecuteUiOperation("update the canvas", UpdateCanvas);
            })) _canvasUpdateQueued = false;
        }

    }

    private void UpdateCanvas()
    {
        // Preserve existing controls: rebuilding the canvas restarts every WebView and
        // discards in-flight previews whenever a source is added, removed or reordered.
        var sourceItems = SourceCanvas.Children.OfType<DraggableSourceItem>()
            .ToDictionary(item => item.Source);
        var sources = ViewModel.Sources.ToHashSet();
        foreach (var item in sourceItems.Values.Where(item => !sources.Contains(item.Source)))
        {
            item.CleanupOnDelete();
            SourceCanvas.Children.Remove(item);
        }

        // Add sources in reverse order so that items at the top of the list (index 0) appear on top
        for (var index = ViewModel.Sources.Count - 1; index >= 0; index--)
        {
            var source = ViewModel.Sources[index];
            if (sourceItems.TryGetValue(source, out var existingItem))
            {
                Canvas.SetZIndex(existingItem, ViewModel.Sources.Count - index);
                continue;
            }
            var item = new DraggableSourceItem { Source = source };
            item.DragStarted += OnDraggableItemDragStarted;
            item.Tapped += OnDraggableItemTapped;
            item.DeleteRequested += OnSourceDeleteRequested;
            item.CopyRequested += OnSourceCopyRequested;
            item.PasteRequested += OnSourcePasteRequested;
            item.CenterRequested += OnSourceCenterRequested;
            item.EditRequested += OnSourceEditRequested;

            SourceCanvas.Children.Add(item);
            Canvas.SetZIndex(item, ViewModel.Sources.Count - index);
            item.RefreshPosition();
        }

        // Initialize group selection control if not already created
        if (_groupSelectionControl == null)
        {
            _groupSelectionControl = new GroupSelectionControl();
            _groupSelectionControl.BoundsChanged += OnGroupSelectionBoundsChangedFromControl;
            SourceCanvas.Children.Add(_groupSelectionControl);
            Canvas.SetZIndex(_groupSelectionControl, GroupOverlayZIndex);
        }
        Canvas.SetZIndex(SelectionRectangle, SelectionOverlayZIndex);

        UpdateListViewSelection();
        UpdateSelectionOnCanvas();
    }

    public void OnNavigatedTo(object parameter)
    {
        SubscribeViewModel();
        if (IsLoaded) ExecuteUiOperation("update the canvas", UpdateCanvas);
    }

    public void OnNavigatedFrom()
    {
        UnsubscribeViewModel();
        EndCanvasInteraction();
    }


}
