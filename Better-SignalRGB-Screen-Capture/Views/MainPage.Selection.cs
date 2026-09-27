using System.Collections.Specialized;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class MainPage
{
    private void CancelCanvasInteraction(object sender, PointerRoutedEventArgs e)
    {
        if (_backgroundGesture?.PointerId != e.Pointer.PointerId) return;
        EndCanvasInteraction();
    }

    private void EndCanvasInteraction()
    {
        _backgroundGesture = null;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SourceCanvas.ReleasePointerCaptures();
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

    }

    private void SourceCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_backgroundGesture != null) return;
        var properties = e.GetCurrentPoint(SourceCanvas).Properties;
        if (properties.IsMiddleButtonPressed)
        {
            if (!SourceCanvas.CapturePointer(e.Pointer)) return;
            _backgroundGesture = new(e.Pointer.PointerId, BackgroundGestureKind.Pan,
                e.GetCurrentPoint(CanvasScrollViewer).Position,
                CanvasScrollViewer.HorizontalOffset, CanvasScrollViewer.VerticalOffset);
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        var hitElement = e.OriginalSource as DependencyObject;
        while (hitElement != null && hitElement != SourceCanvas)
        {
            if (hitElement is DraggableSourceItem or GroupSelectionControl) return;
            hitElement = VisualTreeHelper.GetParent(hitElement);
        }
        if (!SourceCanvas.CapturePointer(e.Pointer)) return;
        Focus(FocusState.Programmatic);
        var point = e.GetCurrentPoint(SourceCanvas).Position;
        var additive = IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Shift);
        if (!additive) ClearSelection();
        _backgroundGesture = new(e.Pointer.PointerId, BackgroundGestureKind.Select, point, AddToSelection: additive);
        Canvas.SetLeft(SelectionRectangle, point.X);
        Canvas.SetTop(SelectionRectangle, point.Y);
        SelectionRectangle.Width = SelectionRectangle.Height = 0;
        SelectionRectangle.Visibility = Visibility.Visible;
        Canvas.SetZIndex(SelectionRectangle, SelectionOverlayZIndex);
        e.Handled = true;
    }

    private static bool IsKeyDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key)
        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void SourceCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_backgroundGesture is not { } gesture || gesture.PointerId != e.Pointer.PointerId) return;
        if (gesture.Kind == BackgroundGestureKind.Pan)
        {
            var position = e.GetCurrentPoint(CanvasScrollViewer).Position;
            CanvasScrollViewer.ChangeView(gesture.ScrollX + gesture.Start.X - position.X,
                gesture.ScrollY + gesture.Start.Y - position.Y, null, true);
        }
        else
        {
            var position = e.GetCurrentPoint(SourceCanvas).Position;
            Canvas.SetLeft(SelectionRectangle, Math.Min(gesture.Start.X, position.X));
            Canvas.SetTop(SelectionRectangle, Math.Min(gesture.Start.Y, position.Y));
            SelectionRectangle.Width = Math.Abs(gesture.Start.X - position.X);
            SelectionRectangle.Height = Math.Abs(gesture.Start.Y - position.Y);
        }
        e.Handled = true;
    }

    private void MainPage_PointerReleased_ForSelection(object sender, PointerRoutedEventArgs e)
    {
        if (_backgroundGesture is not { } gesture || gesture.PointerId != e.Pointer.PointerId) return;
        if (gesture.Kind == BackgroundGestureKind.Select && SelectionRectangle.Width > 3 && SelectionRectangle.Height > 3)
        {
            var bounds = new Rect(Canvas.GetLeft(SelectionRectangle), Canvas.GetTop(SelectionRectangle),
                SelectionRectangle.Width, SelectionRectangle.Height);
            var selected = SourceCanvas.Children.OfType<DraggableSourceItem>()
                .Where(item => Helpers.SourceGeometry.IntersectsVisibleArea(item.Source, bounds)).Select(item => item.Source);
            SelectMultipleItems(selected, gesture.AddToSelection);
        }
        EndCanvasInteraction();
        e.Handled = true;
    }

    private void SourceCanvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var menuFlyout = new MenuFlyout();

        var pasteItem = new MenuFlyoutItem { Text = "Paste", Icon = new FontIcon { Glyph = "\uE77F" } };
        pasteItem.Click += async (s, a) => await ExecuteUiOperationAsync("paste sources", () => ViewModel.PasteSourceCommand.ExecuteAsync(null));
        pasteItem.IsEnabled = ViewModel.PasteSourceCommand.CanExecute(null);
        menuFlyout.Items.Add(pasteItem);

        menuFlyout.Items.Add(new MenuFlyoutSeparator());

        var resetCanvasItem = new MenuFlyoutItem { Text = "Reset Canvas", Icon = new FontIcon { Glyph = "\uE777" } };
        resetCanvasItem.Click += async (s, a) => await ExecuteUiOperationAsync("reset the canvas", () => ViewModel.ResetCanvasCommand.ExecuteAsync(null));
        menuFlyout.Items.Add(resetCanvasItem);

        menuFlyout.ShowAt(SourceCanvas, e.GetPosition(SourceCanvas));
    }

    private async void MainPage_KeyDown(object sender, KeyRoutedEventArgs e) =>
        await ExecuteUiOperationAsync("apply the canvas shortcut", () => HandleCanvasKeyAsync(e));

    private async Task HandleCanvasKeyAsync(KeyRoutedEventArgs e)
    {
        if (e.Handled || !ViewModel.IsCanvasEditable) return;
        var focusedElement = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focusedElement != null && focusedElement != this)
        {
            if (focusedElement is TextBox or RichEditBox or PasswordBox or NumberBox or WebView2) return;
            focusedElement = VisualTreeHelper.GetParent(focusedElement);
        }
        var isCtrlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (!isCtrlDown && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            var step = IsKeyDown(VirtualKey.Shift) ? 10 : 1;
            var dx = e.Key == VirtualKey.Left ? -step : e.Key == VirtualKey.Right ? step : 0;
            var dy = e.Key == VirtualKey.Up ? -step : e.Key == VirtualKey.Down ? step : 0;
            e.Handled = true;
            await ViewModel.NudgeSelectionAsync(dx, dy);
        }
        else if (e.Key == VirtualKey.Delete)
        {
            e.Handled = true;
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

                if (await ShowSourceDialogAsync(dialog) == ContentDialogResult.Primary)
                {
                    await ViewModel.DeleteSourceCommand.ExecuteAsync(selectedSources);
                }
            }
            e.Handled = true;
        }
        else if (isCtrlDown && e.Key == VirtualKey.C)
        {
            e.Handled = true;
            var selectedSources = ViewModel.Sources.Where(s => s.IsSelected).ToList();
            if (selectedSources.Any())
                ViewModel.CopySourceCommand.Execute(selectedSources);
        }
        else if (isCtrlDown && e.Key == VirtualKey.V)
        {
            e.Handled = true;
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
            var cancelled = _groupSelectionControl?.TryCancelInteraction() == true;
            foreach (var item in SourceCanvas.Children.OfType<DraggableSourceItem>())
                cancelled |= item.TryCancelInteraction();
            if (_backgroundGesture != null) { EndCanvasInteraction(); cancelled = true; }
            if (!cancelled) ClearSelection();
            e.Handled = true;
        }
    }

    private void SourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isReorderingSources) return;
        ViewModel.UpdateSelectedSources(SourcesListView.SelectedItems.Cast<SourceItem>());
        UpdateSelectionOnCanvas();
    }

    private void OnViewModelSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_selectionUpdateQueued) return;
        _selectionUpdateQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _selectionUpdateQueued = false;
            if (!IsLoaded) return;
            ExecuteUiOperation("update source selection", () =>
            {
                UpdateListViewSelection();
                UpdateSelectionOnCanvas();
            });
        })) _selectionUpdateQueued = false;
    }

    private void UpdateSelectionInViewModel()
    {
        var selected = ViewModel.Sources.Where(s => s.IsSelected).ToList();
        ViewModel.UpdateSelectedSources(selected);
    }

    private void UpdateListViewSelection()
    {
        SourcesListView.SelectionChanged -= SourceSelectionChanged;
        try
        {
            var selected = ViewModel.SelectedSources.ToHashSet();
            foreach (var stale in SourcesListView.SelectedItems.Cast<SourceItem>().Where(s => !selected.Contains(s)).ToArray())
                SourcesListView.SelectedItems.Remove(stale);
            foreach (var source in selected)
                if (!SourcesListView.SelectedItems.Contains(source)) SourcesListView.SelectedItems.Add(source);
        }
        finally { SourcesListView.SelectionChanged += SourceSelectionChanged; }
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

    // Central Selection Management
    public void SelectSourceItem(SourceItem sourceItem, bool isMultiSelect)
    {
        if (!ViewModel.Sources.Contains(sourceItem)) return;

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
        // First clear the IsSelected flags on all sources
        foreach (var source in ViewModel.Sources)
        {
            source.IsSelected = false;
        }

        // Then update the ViewModel and UI
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

    private void UpdateGroupSelection()
    {
        if (_groupSelectionControl == null) return;

        var selectedSources = ViewModel.Sources.Where(s => s.IsSelected).ToList();
        _groupSelectionControl.UpdateSelection(selectedSources);

        // Update ViewModel with current group bounds when selection changes
        if (selectedSources.Count >= 2)
        {
            var bounds = _groupSelectionControl.GetGroupBounds();
            ViewModel.UpdateGroupSelectionBounds(bounds.x, bounds.y, bounds.width, bounds.height, bounds.rotation);
        }
    }

    private void OnSourcesMoved(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded) ExecuteUiOperation("update group selection", UpdateGroupSelection);
        });
    }

    private void OnGroupSelectionBoundsChangedFromViewModel()
    {
        // Update GroupSelectionControl when ViewModel properties change
        if (_groupSelectionControl != null && ViewModel.IsMultiSelect)
        {
            _groupSelectionControl.SetGroupBounds(
                ViewModel.GroupSelectionX,
                ViewModel.GroupSelectionY,
                ViewModel.GroupSelectionWidth,
                ViewModel.GroupSelectionHeight,
                ViewModel.GroupSelectionRotation,
                true);
        }
    }

    private void OnGroupSelectionBoundsChangedFromControl(object? sender, (double x, double y, double width, double height, double rotation) bounds)
    {
        // Update ViewModel when GroupSelectionControl bounds change
        ViewModel.UpdateGroupSelectionBounds(bounds.x, bounds.y, bounds.width, bounds.height, bounds.rotation);
    }
}
