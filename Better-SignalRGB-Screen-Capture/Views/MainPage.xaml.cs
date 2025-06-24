using System.Threading.Tasks;
using System.Collections.Specialized;
using System.Linq;
using Better_SignalRGB_Screen_Capture.ViewModels;
using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel
    {
        get;
    }

    private bool _isPreviewMode = false;

    public MainPage()
    {
        ViewModel = App.GetService<MainViewModel>();
        InitializeComponent();
        
        // Subscribe to collection changes to update canvas
        ViewModel.Sources.CollectionChanged += OnSourcesCollectionChanged;
        
        // Initialize canvas with existing sources
        UpdateCanvas();
    }

    private async void Add_Sources(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var dlg = new AddSourceDialog
        {
            XamlRoot = this.XamlRoot   // always set this in WinUI 3
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            // Create a new source item from dialog results
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
        
        // Get the friendly name from the dialog
        var friendlyName = dialog.FriendlyName;
        if (!string.IsNullOrEmpty(friendlyName))
        {
            source.Name = friendlyName;
        }
        
        // Determine source type and set properties based on dialog results
        if (!string.IsNullOrEmpty(dialog.SelectedMonitorDeviceId))
        {
            source.Type = SourceType.Monitor;
            source.MonitorDeviceId = dialog.SelectedMonitorDeviceId;
            
            // Set default name if no friendly name provided
            if (string.IsNullOrEmpty(source.Name))
            {
                source.Name = $"Monitor {dialog.SelectedMonitorDeviceId}";
            }
        }
        else if (!string.IsNullOrEmpty(dialog.SelectedProcessPath))
        {
            source.Type = SourceType.Process;
            // Don't save ProcessId - it's not persistent
            source.ProcessId = null;
            source.ProcessPath = dialog.SelectedProcessPath;
            
            // Set default name if no friendly name provided
            if (string.IsNullOrEmpty(source.Name))
            {
                source.Name = System.IO.Path.GetFileNameWithoutExtension(dialog.SelectedProcessPath);
            }
        }
        else if (dialog.SelectedRegion.HasValue)
        {
            source.Type = SourceType.Region;
            source.RegionBounds = dialog.SelectedRegion;
            var region = dialog.SelectedRegion.Value;
            
            // Set default name if no friendly name provided
            if (string.IsNullOrEmpty(source.Name))
            {
                source.Name = $"Region {region.Width}x{region.Height}";
            }
        }
        else if (!string.IsNullOrEmpty(dialog.SelectedWebcamDeviceId))
        {
            source.Type = SourceType.Webcam;
            source.WebcamDeviceId = dialog.SelectedWebcamDeviceId;
            
            // Set default name if no friendly name provided
            if (string.IsNullOrEmpty(source.Name))
            {
                source.Name = "Webcam Source";
            }
        }
        else
        {
            return null; // No valid source selected
        }
        
        return source;
    }
    
    private void OnSourcesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateCanvas();
    }
    
    private void UpdateCanvas()
    {
        // Clear existing draggable items
        var itemsToRemove = SourceCanvas.Children.OfType<DraggableSourceItem>().ToList();
        foreach (var item in itemsToRemove)
        {
            SourceCanvas.Children.Remove(item);
        }
        
        // Add new draggable items for each source
        foreach (var source in ViewModel.Sources)
        {
            var draggableItem = new DraggableSourceItem
            {
                Source = source
            };
            
            // Subscribe to events
            draggableItem.PositionChanged += OnSourcePositionChanged;
            draggableItem.EditRequested += OnSourceEditRequested;
            draggableItem.DeleteRequested += OnSourceDeleteRequested;
            
            // Add to canvas first
            SourceCanvas.Children.Add(draggableItem);
            
            // Set position immediately after adding to canvas
            Canvas.SetLeft(draggableItem, source.CanvasX);
            Canvas.SetTop(draggableItem, source.CanvasY);
            draggableItem.Width = source.CanvasWidth;
            draggableItem.Height = source.CanvasHeight;
            
            // Also ensure position is refreshed on loaded (as backup)
            draggableItem.Loaded += (s, e) => 
            {
                if (s is DraggableSourceItem item)
                {
                    item.RefreshPosition();
                }
            };
        }
        
        // Force a final position refresh for all items after they've all been added
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            foreach (var draggableItem in SourceCanvas.Children.OfType<DraggableSourceItem>())
            {
                draggableItem.ForcePositionUpdate();
            }
        });
    }
    
    private async void OnSourcePositionChanged(object? sender, SourceItem source)
    {
        // Update the source position in the view model (this will save to settings)
        await ViewModel.UpdateSourcePositionCommand.ExecuteAsync(source);
    }
    
    private async void OnSourceEditRequested(object? sender, SourceItem source)
    {
        // Open edit dialog with pre-filled data
        var dlg = new AddSourceDialog(source)
        {
            XamlRoot = this.XamlRoot
        };
        
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            // Update source with new data from dialog
            var updatedSource = CreateSourceFromDialog(dlg);
            if (updatedSource != null)
            {
                // Copy the new values but keep the same ID and canvas position
                updatedSource.Id = source.Id;
                updatedSource.CanvasX = source.CanvasX;
                updatedSource.CanvasY = source.CanvasY;
                updatedSource.CanvasWidth = source.CanvasWidth;
                updatedSource.CanvasHeight = source.CanvasHeight;
                
                // Replace the source in the collection
                var index = ViewModel.Sources.IndexOf(source);
                if (index >= 0)
                {
                    ViewModel.Sources[index] = updatedSource;
                    await ViewModel.EditSourceCommand.ExecuteAsync(updatedSource);
                }
            }
        }
    }
    
    private async void OnSourceDeleteRequested(object? sender, SourceItem source)
    {
        // Show confirmation dialog
        var dialog = new ContentDialog
        {
            Title = "Delete Source",
            Content = $"Are you sure you want to delete '{source.DisplayName}'?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteSourceCommand.ExecuteAsync(source);
        }
    }
    
    private void EditSource_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Button button && button.Tag is SourceItem source)
        {
            OnSourceEditRequested(sender, source);
        }
    }
    
    private void DeleteSource_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Button button && button.Tag is SourceItem source)
        {
            OnSourceDeleteRequested(sender, source);
        }
    }
    
    private void PreviewModeToggle_Click(object sender, RoutedEventArgs e)
    {
        _isPreviewMode = !_isPreviewMode;
        
        if (_isPreviewMode)
        {
            // Switch to live preview mode
            SourceCanvas.Visibility = Visibility.Collapsed;
            PreviewCanvas.Visibility = Visibility.Visible;
            PreviewModeToggle.Content = "Switch to Layout";
            CanvasModeTitle.Text = "Live Preview";
            
            UpdatePreviewCanvas();
        }
        else
        {
            // Switch to layout mode
            SourceCanvas.Visibility = Visibility.Visible;
            PreviewCanvas.Visibility = Visibility.Collapsed;
            PreviewModeToggle.Content = "Switch to Live Preview";
            CanvasModeTitle.Text = "Layout Preview";
        }
    }
    
    private void UpdatePreviewCanvas()
    {
        // Clear existing preview elements
        PreviewCanvas.Children.Clear();
        
        // Add live preview elements for each source
        foreach (var source in ViewModel.Sources)
        {
            var previewElement = new WebView2
            {
                Width = source.CanvasWidth,
                Height = source.CanvasHeight
            };
            
            // Set the source URI to the MJPEG stream
            if (!string.IsNullOrEmpty(ViewModel.StreamingUrl))
            {
                previewElement.Source = new Uri(ViewModel.StreamingUrl);
            }
            
            Canvas.SetLeft(previewElement, source.CanvasX);
            Canvas.SetTop(previewElement, source.CanvasY);
            
            PreviewCanvas.Children.Add(previewElement);
        }
    }
}
