using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }
    private bool _isSelecting;
    private Point _selectionStartPoint;
    private Dictionary<Guid, Point> _dragStartPositions = new();
    private bool _isPanning;
    private Point _panStartPoint;
    private double _panStartScrollX;
    private double _panStartScrollY;
    private GroupSelectionControl? _groupSelectionControl;

    public MainPage()
    {
        ViewModel = App.GetService<MainViewModel>();
        InitializeComponent();
        ViewModel.Sources.CollectionChanged += OnSourcesCollectionChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.SourcesMoved += OnSourcesMoved;
        Loaded += (s, e) =>
        {
            UpdateCanvas();
            // Ensure the canvas can receive keyboard focus
            ContentArea.Focus(FocusState.Programmatic);
            
            // Subscribe to zoom changes to sync the slider
            if (CanvasScrollViewer != null)
            {
                CanvasScrollViewer.ViewChanged += CanvasScrollViewer_ViewChanged;
                
                // Initialize zoom slider with current zoom factor
                if (ZoomSlider != null && ZoomPercentageText != null)
                {
                    ZoomSlider.Value = CanvasScrollViewer.ZoomFactor;
                    ZoomPercentageText.Text = $"{CanvasScrollViewer.ZoomFactor * 100:F0}%";
                }
            }

            // Sync initial selection from loaded data
            UpdateSelectionInViewModel();
            UpdateListViewSelection();
            UpdateSelectionOnCanvas();
        };
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsPreviewing))
        {
            UpdatePreviewCanvas();
        }
        else if (e.PropertyName == nameof(ViewModel.IsPasting) && !ViewModel.IsPasting)
        {
            // When paste is done, sync UI with the final selection from the ViewModel
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateListViewSelection();
                UpdateSelectionOnCanvas();
            });
        }
    }

    private async void Add_Sources(object sender, RoutedEventArgs e)
    {
        var dlg = new AddSourceDialog { XamlRoot = XamlRoot };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            var newSource = CreateSourceFromDialog(dlg);
            if (newSource != null)
            {
                await ViewModel.AddSourceCommand.ExecuteAsync(newSource);
            }
        }
    }

    private SourceItem? CreateSourceFromDialog(AddSourceDialog dialog)
    {
        var source = new SourceItem();
        var friendlyName = dialog.FriendlyName;
        if (!string.IsNullOrEmpty(friendlyName))
        {
            source.Name = friendlyName;
        }

        if (!string.IsNullOrEmpty(dialog.SelectedMonitorDeviceId))
        {
            source.Type = SourceType.Monitor;
            source.MonitorDeviceId = dialog.SelectedMonitorDeviceId;
            if (string.IsNullOrEmpty(source.Name))
                source.Name = $"Monitor {dialog.SelectedMonitorDeviceId}";
        }
        else if (!string.IsNullOrEmpty(dialog.SelectedProcessPath))
        {
            source.Type = SourceType.Process;
            source.ProcessId = null;
            source.ProcessPath = dialog.SelectedProcessPath;
            if (string.IsNullOrEmpty(source.Name))
                source.Name = System.IO.Path.GetFileNameWithoutExtension(dialog.SelectedProcessPath);
        }
        else if (dialog.SelectedRegion.HasValue)
        {
            source.Type = SourceType.Region;
            source.RegionBounds = dialog.SelectedRegion;
            var region = dialog.SelectedRegion.Value;
            if (string.IsNullOrEmpty(source.Name))
                source.Name = $"Region {region.Width}x{region.Height}";
        }
        else if (!string.IsNullOrEmpty(dialog.SelectedWebcamDeviceId))
        {
            source.Type = SourceType.Webcam;
            source.WebcamDeviceId = dialog.SelectedWebcamDeviceId;
            if (string.IsNullOrEmpty(source.Name))
                source.Name = "Webcam Source";
        }
        else if (!string.IsNullOrEmpty(dialog.WebsiteUrl))
        {
            source.Type = SourceType.Website;
            source.WebsiteUrl = dialog.WebsiteUrl;
            if (string.IsNullOrEmpty(source.Name))
                source.Name = dialog.WebsiteUrl;
        }
        else
        {
            return null;
        }
        return source;
    }

    private void OnSourcesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateCanvas();

        // If a paste operation is in progress, do nothing. The ViewModel will manage the final state.
        if (ViewModel.IsPasting)
        {
            return;
        }

        // If an item was added, select it
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null && e.NewItems.Count > 0)
        {
            if (e.NewItems[0] is SourceItem newItem)
            {
                // This call correctly sets IsSelected, updates the view model's
                // SelectedSources collection, and updates all UI parts (canvas and list view)
                SelectSourceItem(newItem, isMultiSelect: false);
            }
        }
    }

    private async void OnSourceEditRequested(DraggableSourceItem sender, RoutedEventArgs e)
    {
        await EditSourceAsync(sender.Source);
    }

    private async Task EditSourceAsync(SourceItem sourceToEdit)
    {
        var dialog = new AddSourceDialog(sourceToEdit)
        {
            XamlRoot = this.XamlRoot,
            Title = "Edit Source"
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            UpdateSourceFromDialog(sourceToEdit, dialog);
            await ViewModel.SaveSourcesAsync();
            
            // The canvas needs to be updated if the source type changed display text
            UpdateCanvas();
        }
    }

    private void UpdateSourceFromDialog(SourceItem source, AddSourceDialog dialog)
    {
        source.Name = dialog.FriendlyName ?? "Unnamed Source";
        source.Type = dialog.SelectedSourceType;

        switch (source.Type)
        {
            case SourceType.Monitor:
                source.MonitorDeviceId = dialog.SelectedMonitorDeviceId;
                break;
            case SourceType.Process:
                source.ProcessId = dialog.SelectedProcessId;
                source.ProcessPath = dialog.SelectedProcessPath;
                break;
            case SourceType.Region:
                source.RegionBounds = dialog.SelectedRegion;
                break;
            case SourceType.Webcam:
                source.WebcamDeviceId = dialog.SelectedWebcamDeviceId;
                break;
            case SourceType.Website:
                source.WebsiteUrl = dialog.WebsiteUrl;
                break;
        }
    }

    private async void OnSourceDeleteRequested(DraggableSourceItem sender, RoutedEventArgs e)
    {
        if (sender.Source == null) return;

        // If the item to be deleted is part of a multiple selection, ask to delete all selected items.
        if (ViewModel.SelectedSources.Count > 1 && ViewModel.SelectedSources.Contains(sender.Source))
        {
            var dialog = new ContentDialog
            {
                Title = "Delete Multiple Sources",
                Content = $"You have {ViewModel.SelectedSources.Count} sources selected. Do you want to delete all of them?",
                PrimaryButtonText = "Delete All",
                SecondaryButtonText = "Delete Only This",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteSourceCommand.ExecuteAsync(ViewModel.SelectedSources.ToList());
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await ViewModel.DeleteSourceCommand.ExecuteAsync(sender.Source);
            }
        }
        else
        {
            var dialog = new ContentDialog
            {
                Title = "Delete Source",
                Content = $"Are you sure you want to delete '{sender.Source.DisplayName}'?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteSourceCommand.ExecuteAsync(sender.Source);
            }
        }
    }

    private void OnSourceCopyRequested(DraggableSourceItem sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedSources.Count > 1 && ViewModel.SelectedSources.Contains(sender.Source))
        {
            ViewModel.CopySourceCommand.Execute(ViewModel.SelectedSources.ToList());
        }
        else if (sender.Source != null)
        {
            ViewModel.CopySourceCommand.Execute(sender.Source);
        }
    }

    private async Task OnSourcePasteRequested(DraggableSourceItem sender, RoutedEventArgs e)
    {
        await ViewModel.PasteSourceCommand.ExecuteAsync(null);
    }

    private async void OnSourceCenterRequested(DraggableSourceItem sender, RoutedEventArgs e)
    {
        // If the item is part of a multi-selection, pass null to the command
        // to indicate that the whole selection should be centered as a group.
        if (ViewModel.SelectedSources.Count > 1 && sender.Source.IsSelected)
        {
            await ViewModel.CenterSourceCommand.ExecuteAsync(null);
        }
        else
        {
            // Otherwise, just center the single source that was clicked.
            await ViewModel.CenterSourceCommand.ExecuteAsync(sender.Source);
        }
    }

    private void OnDraggableItemTapped(object sender, TappedRoutedEventArgs e)
    {
        // Selection is now handled in the DraggableSourceItem's OnPointerPressed
        e.Handled = true;
    }

    private void OnDraggableItemDragStarted(object? sender, EventArgs e)
    {
        if (sender is DraggableSourceItem draggedItem && draggedItem.Source != null)
        {
            // Ensure the dragged item is selected if it's not already part of the selection
            if (!ViewModel.SelectedSources.Contains(draggedItem.Source))
            {
                // If not holding Ctrl/Shift, select just this item
                // If holding modifier keys, add to selection
                var isCtrlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                var isShiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                
                SelectSourceItem(draggedItem.Source, isCtrlDown || isShiftDown);
            }
        }

        _dragStartPositions.Clear();
        foreach (var source in ViewModel.SelectedSources)
        {
            _dragStartPositions[source.Id] = new Point(source.CanvasX, source.CanvasY);
        }
    }

    private void OnDraggableItemDragDelta(object? sender, Point e)
    {
        if (sender is DraggableSourceItem item && _dragStartPositions.TryGetValue(item.Source.Id, out var startPos))
        {
            var newX = startPos.X + e.X;
            var newY = startPos.Y + e.Y;

            // The clamping logic is now correctly handled inside DraggableSourceItem.
            // The safety check below was incorrect as it didn't account for cropping or rotation,
            // causing the visible part of the rectangle to be pushed back inside the canvas
            // even when only the invisible part was outside.
            
            // We just need to update the source's position. DraggableSourceItem handles the rest.
            item.Source.CanvasX = (int)newX;
            item.Source.CanvasY = (int)newY;
        }
        else if (sender is GroupSelectionControl gsc && _dragStartPositions.TryGetValue(Guid.Empty, out var groupStartPos))
        {
            var newGroupX = groupStartPos.X + e.X;
            var newGroupY = groupStartPos.Y + e.Y;

            // Move all items in the group by the delta
            foreach (var source in gsc.SelectedSources)
            {
                if (_dragStartPositions.TryGetValue(source.Id, out var itemStartPos))
                {
                    source.CanvasX = (int)Math.Round(itemStartPos.X + e.X);
                    source.CanvasY = (int)Math.Round(itemStartPos.Y + e.Y);
                }
            }
        }
    }

    private void SourceCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        var properties = e.GetCurrentPoint(SourceCanvas).Properties;
        
        // Handle middle mouse button for panning
        if (properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panStartPoint = e.GetCurrentPoint(CanvasScrollViewer).Position;
            _panStartScrollX = CanvasScrollViewer.HorizontalOffset;
            _panStartScrollY = CanvasScrollViewer.VerticalOffset;
            SourceCanvas.CapturePointer(e.Pointer);
            
            // Handle release on the page to capture events outside the canvas
            this.PointerReleased += MainPage_PointerReleased_ForSelection;
            e.Handled = true;
            return;
        }
        
        if (!properties.IsLeftButtonPressed) return;

        // Check if clicking on an item
        var point = e.GetCurrentPoint(SourceCanvas).Position;
        var hitTestResult = VisualTreeHelper.FindElementsInHostCoordinates(point, SourceCanvas);
        var hitItem = hitTestResult.OfType<DraggableSourceItem>().FirstOrDefault();
        
        if (hitItem != null)
        {
            // Item interaction is handled by the item itself
            return;
        }

        // Clicking on empty canvas
        var isCtrlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var isShiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        
        if (!isCtrlDown && !isShiftDown)
        {
            // Clear selection when clicking empty space (unless modifier keys are held)
            ClearSelection();
        }

        // Start zone selection
        _isSelecting = true;
        _selectionStartPoint = point;
        Canvas.SetLeft(SelectionRectangle, point.X);
        Canvas.SetTop(SelectionRectangle, point.Y);
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
        SelectionRectangle.Visibility = Visibility.Visible;
        
        // Ensure the selection rectangle is brought to front
        Canvas.SetZIndex(SelectionRectangle, 1000);
        
        // Capture pointer for selection
        SourceCanvas.CapturePointer(e.Pointer);
        
        // Handle release on the page to capture events outside the canvas
        this.PointerReleased += MainPage_PointerReleased_ForSelection;
        e.Handled = true;
    }

    private void SourceCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Handle panning
        if (_isPanning)
        {
            var panPoint = e.GetCurrentPoint(CanvasScrollViewer).Position;
            var deltaX = _panStartPoint.X - panPoint.X;
            var deltaY = _panStartPoint.Y - panPoint.Y;
            
            var newScrollX = _panStartScrollX + deltaX;
            var newScrollY = _panStartScrollY + deltaY;
            
            CanvasScrollViewer.ChangeView(newScrollX, newScrollY, null, true);
            e.Handled = true;
            return;
        }
        
        if (!_isSelecting) return;
        
        var currentPoint = e.GetCurrentPoint(SourceCanvas).Position;
        var x = Math.Min(_selectionStartPoint.X, currentPoint.X);
        var y = Math.Min(_selectionStartPoint.Y, currentPoint.Y);
        var width = Math.Abs(_selectionStartPoint.X - currentPoint.X);
        var height = Math.Abs(_selectionStartPoint.Y - currentPoint.Y);
        Canvas.SetLeft(SelectionRectangle, x);
        Canvas.SetTop(SelectionRectangle, y);
        SelectionRectangle.Width = width;
        SelectionRectangle.Height = height;
    }

    private void MainPage_PointerReleased_ForSelection(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        // Handle panning release
        if (_isPanning)
        {
            _isPanning = false;
            SourceCanvas.ReleasePointerCapture(e.Pointer);
            this.PointerReleased -= MainPage_PointerReleased_ForSelection;
            e.Handled = true;
            return;
        }
        
        if (_isSelecting)
        {
            _isSelecting = false;
            SelectionRectangle.Visibility = Visibility.Collapsed;
            
            // Release pointer capture
            SourceCanvas.ReleasePointerCapture(e.Pointer);
            
            var selectionRect = new Rect(Canvas.GetLeft(SelectionRectangle), Canvas.GetTop(SelectionRectangle), SelectionRectangle.Width, SelectionRectangle.Height);

            // Only proceed with selection if the rectangle has meaningful size
            if (selectionRect.Width > 3 && selectionRect.Height > 3)
            {
                var selectedSources = new List<SourceItem>();
                foreach (var item in SourceCanvas.Children.OfType<DraggableSourceItem>())
                {
                    var itemRect = new Rect(Canvas.GetLeft(item), Canvas.GetTop(item), item.ActualWidth, item.ActualHeight);
                    if (RectsIntersect(selectionRect, itemRect))
                    {
                        selectedSources.Add(item.Source);
                    }
                }
                
                var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                
                if (selectedSources.Any())
                {
                    SelectMultipleItems(selectedSources, ctrlPressed || shiftPressed);
                }
            }
        }

        this.PointerReleased -= MainPage_PointerReleased_ForSelection;
        e.Handled = true;
    }

    private void SourceCanvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var menuFlyout = new MenuFlyout();

        var pasteItem = new MenuFlyoutItem { Text = "Paste", Icon = new FontIcon { Glyph = "\uE77F" } };
        pasteItem.Click += async (s, a) => await ViewModel.PasteSourceCommand.ExecuteAsync(null);
        pasteItem.IsEnabled = ViewModel.PasteSourceCommand.CanExecute(null);
        menuFlyout.Items.Add(pasteItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());
        
        var resetCanvasItem = new MenuFlyoutItem { Text = "Reset Canvas", Icon = new FontIcon { Glyph = "\uE777" } };
        resetCanvasItem.Click += async (s, a) => await ViewModel.ResetCanvasCommand.ExecuteAsync(null);
        menuFlyout.Items.Add(resetCanvasItem);

        menuFlyout.ShowAt(SourceCanvas, e.GetPosition(SourceCanvas));
    }

    private bool RectsIntersect(Rect r1, Rect r2) => r1.X < r2.X + r2.Width && r1.X + r1.Width > r2.X && r1.Y < r2.Y + r2.Height && r1.Y + r1.Height > r2.Y;

    /// <summary>
    /// Returns the axis-aligned bounding box (AABB) of a rotated rectangle
    /// </summary>
    /// <param name="center">Center point of the rectangle</param>
    /// <param name="size">Size of the rectangle</param>
    /// <param name="angleDeg">Rotation angle in degrees</param>
    /// <returns>The AABB that fully contains the rotated rectangle</returns>
    private static Rect GetRotatedAabb(Point center, Size size, double angleDeg)
    {
        var angle = angleDeg * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(angle));
        var sin = Math.Abs(Math.Sin(angle));

        var w = size.Width * cos + size.Height * sin;
        var h = size.Width * sin + size.Height * cos;

        return new Rect(
            center.X - w / 2,
            center.Y - h / 2,
            w, h);
    }

    private async void DeleteSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SourceItem source)
        {
            var dialog = new ContentDialog
            {
                Title = "Delete Source",
                Content = $"Are you sure you want to delete '{source.DisplayName}'?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteSourceCommand.ExecuteAsync(source);
            }
        }
    }

    private async void MainPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var isCtrlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        
        if (e.Key == VirtualKey.Delete)
        {
            var selectedSources = ViewModel.Sources.Where(s => s.IsSelected).ToList();
            if (selectedSources.Any())
            {
                var dialog = new ContentDialog
                {
                    Title = selectedSources.Count == 1 ? "Delete Source" : "Delete Multiple Sources",
                    Content = selectedSources.Count == 1 
                        ? $"Are you sure you want to delete '{selectedSources[0].DisplayName}'?"
                        : $"Are you sure you want to delete {selectedSources.Count} selected sources?",
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                    XamlRoot = XamlRoot
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await ViewModel.DeleteSourceCommand.ExecuteAsync(selectedSources);
                }
            }
            e.Handled = true;
        }
        else if (isCtrlDown && e.Key == VirtualKey.C)
        {
            var selectedSources = ViewModel.Sources.Where(s => s.IsSelected).ToList();
            if (selectedSources.Any())
                ViewModel.CopySourceCommand.Execute(selectedSources);
        }
        else if (isCtrlDown && e.Key == VirtualKey.V)
        {
            await ViewModel.PasteSourceCommand.ExecuteAsync(null);
        }
        else if (isCtrlDown && e.Key == VirtualKey.Z)
        {
            await ViewModel.UndoCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (isCtrlDown && e.Key == VirtualKey.Y)
        {
            await ViewModel.RedoCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (isCtrlDown && e.Key == VirtualKey.A)
        {
            // Select all sources
            SelectMultipleItems(ViewModel.Sources, false);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            // Clear selection on Escape
            ClearSelection();
            e.Handled = true;
        }
    }

    private void SourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Prevent recursive updates by temporarily removing the event handler
        SourcesListView.SelectionChanged -= SourceSelectionChanged;
        
        try
        {
            // Sync the IsSelected property on the source items with the ListView selection
            foreach (var item in e.AddedItems.Cast<SourceItem>())
            {
                item.IsSelected = true;
            }
            foreach (var item in e.RemovedItems.Cast<SourceItem>())
            {
                item.IsSelected = false;
            }

            UpdateSelectionOnCanvas();
            UpdateSelectionInViewModel();
        }
        finally
        {
            // Always re-attach the event handler
            SourcesListView.SelectionChanged += SourceSelectionChanged;
        }
    }
    
    private void UpdateSelectionInViewModel()
    {
        var selected = ViewModel.Sources.Where(s => s.IsSelected).ToList();
        ViewModel.UpdateSelectedSources(selected);
    }

    private void UpdateListViewSelection()
    {
        SourcesListView.SelectionChanged -= SourceSelectionChanged;
        SourcesListView.SelectedItems.Clear();
        foreach (var source in ViewModel.Sources.Where(s => s.IsSelected))
        {
            SourcesListView.SelectedItems.Add(source);
        }
        SourcesListView.SelectionChanged += SourceSelectionChanged;
    }

    private void UpdateSelectionOnCanvas()
    {
        foreach (var item in SourceCanvas.Children.OfType<DraggableSourceItem>())
        {
            item.SetSelected(item.Source.IsSelected);
        }
        
        // Update group selection control
        UpdateGroupSelection();
    }

    private void ZoomToFit_Click(object sender, RoutedEventArgs e)
    {
        if (CanvasScrollViewer != null && SourceCanvas.Width > 0 && SourceCanvas.Height > 0)
        {
            var zoomX = CanvasScrollViewer.ViewportWidth / SourceCanvas.Width;
            var zoomY = CanvasScrollViewer.ViewportHeight / SourceCanvas.Height;
            var targetZoom = Math.Min(zoomX, zoomY);
            
            // Calculate scroll position to center the canvas in the viewport
            var scaledCanvasWidth = SourceCanvas.Width * targetZoom;
            var scaledCanvasHeight = SourceCanvas.Height * targetZoom;
            
            var scrollX = (scaledCanvasWidth - CanvasScrollViewer.ViewportWidth) / 2;
            var scrollY = (scaledCanvasHeight - CanvasScrollViewer.ViewportHeight) / 2;
            
            // Let the Grid centering handle small zoom levels automatically
            // Only adjust scroll position if content is larger than viewport
            if (scaledCanvasWidth <= CanvasScrollViewer.ViewportWidth) scrollX = 0;
            if (scaledCanvasHeight <= CanvasScrollViewer.ViewportHeight) scrollY = 0;
            
            CanvasScrollViewer.ChangeView(scrollX, scrollY, (float)targetZoom);
        }
    }

    private void Zoom100_Click(object sender, RoutedEventArgs e)
    {
        if (CanvasScrollViewer != null)
        {
            var currentZoom = CanvasScrollViewer.ZoomFactor;
            var targetZoom = 1.0f;
            
            // Get the center point of the current view in content coordinates
            var viewportCenterX = CanvasScrollViewer.ViewportWidth / 2;
            var viewportCenterY = CanvasScrollViewer.ViewportHeight / 2;
            
            // Convert viewport center to content coordinates at current zoom
            var contentCenterX = (CanvasScrollViewer.HorizontalOffset + viewportCenterX) / currentZoom;
            var contentCenterY = (CanvasScrollViewer.VerticalOffset + viewportCenterY) / currentZoom;
            
            // Calculate where this content point should be positioned at 100% zoom
            var newContentCenterX = contentCenterX * targetZoom;
            var newContentCenterY = contentCenterY * targetZoom;
            
            // Calculate the scroll position to center this point in the viewport
            var newScrollX = newContentCenterX - viewportCenterX;
            var newScrollY = newContentCenterY - viewportCenterY;
            
            CanvasScrollViewer.ChangeView(newScrollX, newScrollY, targetZoom);
        }
    }

    private void UpdatePreviewCanvas()
    {
        var isPreview = ViewModel.IsPreviewing;
        
        CanvasModeTitle.Text = isPreview ? "Live Preview" : "Layout Preview";
        
        // The actual preview will be rendered inside the DraggableSourceItems in a future update.
        // For now, we just toggle the state and title.
        // SourceCanvas.Visibility = isPreview ? Visibility.Collapsed : Visibility.Visible;
        // PreviewCanvas.Visibility = isPreview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDraggableItemRightTapped(DraggableSourceItem? sender, RightTappedRoutedEventArgs e)
    {
        // This is now handled by the DraggableSourceItem's own RightTapped event
        // We can add canvas-level context menu logic here if needed in the future
    }

    private bool _isUpdatingZoomSlider = false;

    private void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (CanvasScrollViewer != null && sender is Slider slider && !_isUpdatingZoomSlider)
        {
            var zoomFactor = (float)slider.Value;
            var currentZoom = CanvasScrollViewer.ZoomFactor;
            
            if (Math.Abs(zoomFactor - currentZoom) < 0.001) return; // Avoid unnecessary updates
            
            // Get the center point of the current view in content coordinates
            var viewportCenterX = CanvasScrollViewer.ViewportWidth / 2;
            var viewportCenterY = CanvasScrollViewer.ViewportHeight / 2;
            
            // Convert viewport center to content coordinates at current zoom
            var contentCenterX = (CanvasScrollViewer.HorizontalOffset + viewportCenterX) / currentZoom;
            var contentCenterY = (CanvasScrollViewer.VerticalOffset + viewportCenterY) / currentZoom;
            
            // Calculate where this content point should be positioned at the new zoom
            var newContentCenterX = contentCenterX * zoomFactor;
            var newContentCenterY = contentCenterY * zoomFactor;
            
            // Calculate the scroll position to center this point in the viewport
            var newScrollX = newContentCenterX - viewportCenterX;
            var newScrollY = newContentCenterY - viewportCenterY;
            
            // Apply the zoom change - let the Grid centering handle small zoom levels automatically
            CanvasScrollViewer.ChangeView(newScrollX, newScrollY, zoomFactor, false); // false = don't animate
            
            // Update the percentage text
            if (ZoomPercentageText != null)
            {
                ZoomPercentageText.Text = $"{slider.Value * 100:F0}%";
            }
        }
    }

    private void CanvasScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (CanvasScrollViewer != null && ZoomSlider != null && ZoomPercentageText != null)
        {
            _isUpdatingZoomSlider = true;
            ZoomSlider.Value = CanvasScrollViewer.ZoomFactor;
            ZoomPercentageText.Text = $"{CanvasScrollViewer.ZoomFactor * 100:F0}%";
            _isUpdatingZoomSlider = false;
        }
    }

    // Central Selection Management
    public void SelectSourceItem(SourceItem sourceItem, bool isMultiSelect)
    {
        if (sourceItem == null) return;

        if (isMultiSelect)
        {
            // Multi-select mode (Ctrl/Shift held)
            sourceItem.IsSelected = !sourceItem.IsSelected;
        }
        else
        {
            // Single-select mode - deselect all others
            foreach (var source in ViewModel.Sources.Where(s => s != sourceItem))
            {
                source.IsSelected = false;
            }
            sourceItem.IsSelected = true;
        }

        UpdateSelectionInViewModel();
        UpdateListViewSelection();
        UpdateSelectionOnCanvas();
    }

    public void ClearSelection()
    {
        ViewModel.UpdateSelectedSources(new List<SourceItem>());
        UpdateListViewSelection();
        UpdateSelectionOnCanvas();
    }

    public void SelectMultipleItems(IEnumerable<SourceItem> items, bool addToSelection = false)
    {
        var selection = addToSelection ? ViewModel.SelectedSources.Union(items).ToList() : items.ToList();
        ViewModel.UpdateSelectedSources(selection);

        UpdateListViewSelection();
        UpdateSelectionOnCanvas();
    }

    private void UpdateCanvas()
    {
        // Don't clear the selection rectangle, just the source items.
        var sourceItems = SourceCanvas.Children.OfType<DraggableSourceItem>().ToList();
        foreach (var item in sourceItems)
        {
            SourceCanvas.Children.Remove(item);
        }

        foreach (var source in ViewModel.Sources)
        {
            var item = new DraggableSourceItem { Source = source };
            item.DragStarted += OnDraggableItemDragStarted;
            item.DragDelta += OnDraggableItemDragDelta;
            item.Tapped += OnDraggableItemTapped;
            item.RightTapped += (s, e) => OnDraggableItemRightTapped(s as DraggableSourceItem, e);
            item.DeleteRequested += OnSourceDeleteRequested;
            item.CopyRequested += OnSourceCopyRequested;
            item.PasteRequested += async (s, e) => await OnSourcePasteRequested(s, e);
            item.CenterRequested += OnSourceCenterRequested;
            item.EditRequested += OnSourceEditRequested;

            SourceCanvas.Children.Add(item);
            item.RefreshPosition();
        }

        // Initialize group selection control if not already created
        if (_groupSelectionControl == null)
        {
            _groupSelectionControl = new GroupSelectionControl();
            _groupSelectionControl.DragStarted += OnGroupSelectionDragStarted;
            _groupSelectionControl.DragDelta += OnGroupSelectionDragDelta;
            SourceCanvas.Children.Add(_groupSelectionControl);
            Canvas.SetZIndex(_groupSelectionControl, 1000); // Ensure it's on top
        }

        UpdateSelectionOnCanvas();
    }

    private void UpdateGroupSelection()
    {
        if (_groupSelectionControl == null) return;

        var selectedSources = ViewModel.Sources.Where(s => s.IsSelected).ToList();
        _groupSelectionControl.UpdateSelection(selectedSources);
    }

    private void OnGroupSelectionDragStarted(object? sender, EventArgs e)
    {
        // Store initial positions for all selected items
        _dragStartPositions.Clear();
        foreach (var source in ViewModel.SelectedSources)
        {
            _dragStartPositions[source.Id] = new Point(source.CanvasX, source.CanvasY);
        }
    }

    private void OnGroupSelectionDragDelta(object? sender, Point e)
    {
        // This is handled by the GroupSelectionControl itself
        // The individual items are updated directly by the control
    }

    private void OnSourcesMoved(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateGroupSelection();
        });
    }
}
