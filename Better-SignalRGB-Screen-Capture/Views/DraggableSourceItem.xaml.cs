using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class DraggableSourceItem : UserControl
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(SourceItem), typeof(DraggableSourceItem), 
            new PropertyMetadata(null, OnSourceChanged));

    public SourceItem Source
    {
        get => (SourceItem)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public event EventHandler<SourceItem>? PositionChanged;
    public event EventHandler<SourceItem>? EditRequested;
    public event EventHandler<SourceItem>? DeleteRequested;

    private bool _isDragging;
    private bool _isResizing;
    private Point _lastPointerPosition;
    private ResizeMode _resizeMode = ResizeMode.None;

    private enum ResizeMode
    {
        None,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    public DraggableSourceItem()
    {
        InitializeComponent();
        
        // Set up event handlers
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        DoubleTapped += OnDoubleTapped;
        RightTapped += OnRightTapped;
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DraggableSourceItem control)
        {
            // Unsubscribe from old source if any
            if (e.OldValue is SourceItem oldSource)
            {
                oldSource.PropertyChanged -= control.OnSourcePropertyChanged;
            }
            
            // Subscribe to new source and update position
            if (e.NewValue is SourceItem newSource)
            {
                newSource.PropertyChanged += control.OnSourcePropertyChanged;
                control.UpdatePosition();
            }
        }
    }

    private void OnSourcePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Update position when canvas properties change
        if (e.PropertyName == nameof(SourceItem.CanvasX) || 
            e.PropertyName == nameof(SourceItem.CanvasY) ||
            e.PropertyName == nameof(SourceItem.CanvasWidth) || 
            e.PropertyName == nameof(SourceItem.CanvasHeight))
        {
            // Use Dispatcher to ensure UI updates happen on UI thread
            DispatcherQueue.TryEnqueue(() => RefreshPosition());
        }
    }

    private void UpdatePosition()
    {
        if (Source != null)
        {
            RefreshPosition();
        }
    }
    
    public void ForcePositionUpdate()
    {
        if (Source != null)
        {
            // Force immediate position update without any conditions
            Canvas.SetLeft(this, Source.CanvasX);
            Canvas.SetTop(this, Source.CanvasY);
            Width = Source.CanvasWidth;
            Height = Source.CanvasHeight;
        }
    }

    public void RefreshPosition()
    {
        if (Source != null)
        {
            Canvas.SetLeft(this, Source.CanvasX);
            Canvas.SetTop(this, Source.CanvasY);
            Width = Source.CanvasWidth;
            Height = Source.CanvasHeight;
        }
    }

    public string GetTypeDisplayString(SourceType type)
    {
        return type switch
        {
            SourceType.Monitor => "Monitor",
            SourceType.Process => "Process",
            SourceType.Region => "Region",
            SourceType.Webcam => "Webcam",
            _ => "Unknown"
        };
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Source == null) return;

        _lastPointerPosition = e.GetCurrentPoint(this).Position;
        
        // Check if clicking on resize handle
        _resizeMode = GetResizeModeFromPosition(_lastPointerPosition);
        
        if (_resizeMode != ResizeMode.None)
        {
            _isResizing = true;
            ProtectedCursor = InputSystemCursor.Create(GetCursorForResizeMode(_resizeMode));
        }
        else
        {
            _isDragging = true;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        }

        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Source == null) return;

        var currentPosition = e.GetCurrentPoint(this).Position;
        
        if (_isDragging && Parent is Canvas canvas)
        {
            var deltaX = currentPosition.X - _lastPointerPosition.X;
            var deltaY = currentPosition.Y - _lastPointerPosition.Y;

            var newX = (int)Math.Max(0, Math.Min(canvas.ActualWidth - ActualWidth, Source.CanvasX + deltaX));
            var newY = (int)Math.Max(0, Math.Min(canvas.ActualHeight - ActualHeight, Source.CanvasY + deltaY));

            Source.CanvasX = newX;
            Source.CanvasY = newY;
            
            Canvas.SetLeft(this, newX);
            Canvas.SetTop(this, newY);
        }
        else if (_isResizing && Parent is Canvas parentCanvas)
        {
            ResizeFromMode(_resizeMode, currentPosition, parentCanvas);
        }
        else
        {
            // Update cursor based on position when not dragging
            var resizeMode = GetResizeModeFromPosition(currentPosition);
            ProtectedCursor = resizeMode != ResizeMode.None 
                ? InputSystemCursor.Create(GetCursorForResizeMode(resizeMode))
                : InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        }

        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging || _isResizing)
        {
            _isDragging = false;
            _isResizing = false;
            _resizeMode = ResizeMode.None;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
            
            PositionChanged?.Invoke(this, Source);
        }

        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Show resize handles
        TopLeftHandle.Opacity = 1;
        TopRightHandle.Opacity = 1;
        BottomLeftHandle.Opacity = 1;
        BottomRightHandle.Opacity = 1;
        
        SourceBorder.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging && !_isResizing)
        {
            // Hide resize handles
            TopLeftHandle.Opacity = 0;
            TopRightHandle.Opacity = 0;
            BottomLeftHandle.Opacity = 0;
            BottomRightHandle.Opacity = 0;
            
            SourceBorder.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        }
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        EditRequested?.Invoke(this, Source);
        e.Handled = true;
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Could show context menu here for edit/delete
        DeleteRequested?.Invoke(this, Source);
        e.Handled = true;
    }

    private ResizeMode GetResizeModeFromPosition(Point position)
    {
        const double handleSize = 12; // Larger hit area than visual handle
        
        if (position.X <= handleSize && position.Y <= handleSize)
            return ResizeMode.TopLeft;
        if (position.X >= ActualWidth - handleSize && position.Y <= handleSize)
            return ResizeMode.TopRight;
        if (position.X <= handleSize && position.Y >= ActualHeight - handleSize)
            return ResizeMode.BottomLeft;
        if (position.X >= ActualWidth - handleSize && position.Y >= ActualHeight - handleSize)
            return ResizeMode.BottomRight;
            
        return ResizeMode.None;
    }

    private InputSystemCursorShape GetCursorForResizeMode(ResizeMode mode)
    {
        return mode switch
        {
            ResizeMode.TopLeft or ResizeMode.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
            ResizeMode.TopRight or ResizeMode.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
            _ => InputSystemCursorShape.Arrow
        };
    }

    private void ResizeFromMode(ResizeMode mode, Point currentPosition, Canvas canvas)
    {
        const int minSize = 60;
        
        switch (mode)
        {
            case ResizeMode.TopLeft:
                var deltaX = _lastPointerPosition.X - currentPosition.X;
                var deltaY = _lastPointerPosition.Y - currentPosition.Y;
                
                var newWidth = (int)Math.Max(minSize, Source.CanvasWidth + deltaX);
                var newHeight = (int)Math.Max(minSize, Source.CanvasHeight + deltaY);
                var newX = (int)Math.Max(0, Source.CanvasX - (newWidth - Source.CanvasWidth));
                var newY = (int)Math.Max(0, Source.CanvasY - (newHeight - Source.CanvasHeight));
                
                Source.CanvasWidth = newWidth;
                Source.CanvasHeight = newHeight;
                Source.CanvasX = newX;
                Source.CanvasY = newY;
                break;
                
            case ResizeMode.TopRight:
                var newWidth2 = (int)Math.Max(minSize, currentPosition.X);
                var newHeight2 = (int)Math.Max(minSize, Source.CanvasHeight + (_lastPointerPosition.Y - currentPosition.Y));
                var newY2 = (int)Math.Max(0, Source.CanvasY - (newHeight2 - Source.CanvasHeight));
                
                Source.CanvasWidth = newWidth2;
                Source.CanvasHeight = newHeight2;
                Source.CanvasY = newY2;
                break;
                
            case ResizeMode.BottomLeft:
                var newWidth3 = (int)Math.Max(minSize, Source.CanvasWidth + (_lastPointerPosition.X - currentPosition.X));
                var newHeight3 = (int)Math.Max(minSize, currentPosition.Y);
                var newX3 = (int)Math.Max(0, Source.CanvasX - (newWidth3 - Source.CanvasWidth));
                
                Source.CanvasWidth = newWidth3;
                Source.CanvasHeight = newHeight3;
                Source.CanvasX = newX3;
                break;
                
            case ResizeMode.BottomRight:
                Source.CanvasWidth = (int)Math.Max(minSize, currentPosition.X);
                Source.CanvasHeight = (int)Math.Max(minSize, currentPosition.Y);
                break;
        }
        
        // Ensure we don't go outside canvas bounds
        if (Source.CanvasX + Source.CanvasWidth > canvas.ActualWidth)
            Source.CanvasWidth = (int)canvas.ActualWidth - Source.CanvasX;
        if (Source.CanvasY + Source.CanvasHeight > canvas.ActualHeight)
            Source.CanvasHeight = (int)canvas.ActualHeight - Source.CanvasY;
        
        UpdatePosition();
    }
} 