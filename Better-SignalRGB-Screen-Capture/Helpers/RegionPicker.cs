using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using WinRT;
using WinUIEx;
using Better_SignalRGB_Screen_Capture.Contracts.Services;

namespace Better_SignalRGB_Screen_Capture.Helpers;

internal static class RegionPicker
{
    public static Task<RectInt32?> PickAsync()
    {
        var tcs = new TaskCompletionSource<RectInt32?>();
        new OverlayWindow(tcs).Activate();
        return tcs.Task;
    }

    // ──────────────────────────────────────────────────────────────────────────
    private sealed class OverlayWindow : WindowEx
    {
        #region interaction-state
        private readonly TaskCompletionSource<RectInt32?> _tcs;
        private Windows.Foundation.Point? _anchor;
        private RectInt32? _rect;
        private Mode _mode = Mode.Drawing;
        private Edge? _resizeEdge;
        private const double HIT = 6;

        private enum Mode
        {
            Drawing, Idle, Moving, Resizing
        }
        [Flags]
        private enum Edge
        {
            L = 1, T = 2, R = 4, B = 8
        }
        #endregion

        #region visuals – root + chrome
        private sealed class CursorCanvas : Canvas
        {
            public void SetCursor(InputCursor c) => ProtectedCursor = c;
        }

        private readonly CursorCanvas _root = new()
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };

        // full-screen dim until the user starts drawing
        private readonly Rectangle _overlay = new()
        {
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 0, 0, 0)),
            Visibility = Visibility.Visible
        };

        // four "bands" that carve a hole for the selection
        private readonly Rectangle _dimT = Dim(), _dimL = Dim(),
                                   _dimR = Dim(), _dimB = Dim();

        private readonly Rectangle _rubber = new()
        {
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed
        };

        private static Rectangle Dim() => new()
        {
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 0, 0, 0))
        };
        #endregion

        private readonly CommandBarFlyout _flyout;

        // ──────────────────────────────────────────────────────────────────────
        public OverlayWindow(TaskCompletionSource<RectInt32?> tcs)
        {
            _tcs = tcs;

            /* Window chrome & transparency */
            IsResizable = false;
            IsMaximizable = false;
            IsMinimizable = false;

            ExtendsContentIntoTitleBar = true;
            SystemBackdrop = new TransparentTintBackdrop();

            // Apply the current app theme to this window
            try
            {
                var themeSelectorService = App.GetService<IThemeSelectorService>();
                _root.RequestedTheme = themeSelectorService.Theme;
            }
            catch
            {
                // Fallback to default theme if service is not available
                // Try to match the main window's theme
                try
                {
                    if (App.MainWindow?.Content is FrameworkElement mainContent)
                    {
                        _root.RequestedTheme = mainContent.ActualTheme switch
                        {
                            ElementTheme.Light => ElementTheme.Light,
                            ElementTheme.Dark => ElementTheme.Dark,
                            _ => ElementTheme.Default
                        };
                    }
                }
                catch { }
            }

            /* visual tree */
            _root.Children.Add(_overlay);
            _root.Children.Add(_dimT); _root.Children.Add(_dimL);
            _root.Children.Add(_dimR); _root.Children.Add(_dimB);
            _root.Children.Add(_rubber);
            Content = _root;

            /* make sure the overlay is sized BEFORE the user can see anything */
            _root.Loaded += (_, _) => CoverEverything();
            
            Activated += (_, _) => _root.SetCursor(InputSystemCursor.Create(InputSystemCursorShape.Cross));

            /* HWND tweaks */
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            InitWindowStyles(hwnd);
            StretchOverDesktop(hwnd);
            BringToFront();
            SetForegroundWindow(hwnd);

            /* input */
            _root.PointerPressed += OnPointerPressed;
            _root.PointerMoved += OnPointerMoved;
            _root.PointerReleased += OnPointerReleased;
            _root.PointerExited += (_, _) => UpdateHoverCursor(null);

            _root.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape) { _rect = null; Close(); }
                else if (e.Key == Windows.System.VirtualKey.Enter && _rect.HasValue)
                    AcceptAndClose();
            };

            _flyout = BuildFlyout();
            Closed += (_, _) => _tcs.TrySetResult(_rect);
            SizeChanged += (_, _) => CoverEverything();
        }

        #region pointer logic
        private void OnPointerPressed(object _, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_overlay.Visibility == Visibility.Visible)
                _overlay.Visibility = Visibility.Collapsed; // first click → start drawing

            var pos = e.GetCurrentPoint(_root).Position;

            if (_mode == Mode.Idle && _rect.HasValue)
            {
                var hit = HitEdge(pos);
                if (hit != null) { _mode = Mode.Resizing; _resizeEdge = hit; }
                else if (InsideRect(pos, _rect.Value)) _mode = Mode.Moving;
                else { _mode = Mode.Drawing; _rubber.Visibility = Visibility.Collapsed; }
            }
            else _mode = Mode.Drawing;

            _anchor = pos;
            _root.CapturePointer(e.Pointer);
            if (_flyout.IsOpen) _flyout.Hide();
            _root.SetCursor(InputSystemCursor.Create(InputSystemCursorShape.Cross));
        }

        private void OnPointerMoved(object _, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var pos = e.GetCurrentPoint(_root).Position;

            if (_anchor is null) { UpdateHoverCursor(pos); return; }

            if (_mode == Mode.Drawing)
            {
                UpdateRubber(Math.Min(pos.X, _anchor.Value.X),
                              Math.Min(pos.Y, _anchor.Value.Y),
                              Math.Abs(pos.X - _anchor.Value.X),
                              Math.Abs(pos.Y - _anchor.Value.Y));
            }
            else if (_mode == Mode.Moving && _rect.HasValue)
            {
                var dx = pos.X - _anchor.Value.X;
                var dy = pos.Y - _anchor.Value.Y;
                var r = _rect.Value;
                UpdateRubber(r.X + dx, r.Y + dy, r.Width, r.Height);
                _anchor = pos;
            }
            else if (_mode == Mode.Resizing && _rect.HasValue && _resizeEdge.HasValue)
            {
                var r = _rect.Value;
                double x = r.X, y = r.Y, w = r.Width, h = r.Height;

                if (_resizeEdge.Value.HasFlag(Edge.L)) { x = Math.Min(pos.X, Right(r)); w = Right(r) - x; }
                if (_resizeEdge.Value.HasFlag(Edge.R)) w = Math.Max(1, pos.X - r.X);
                if (_resizeEdge.Value.HasFlag(Edge.T)) { y = Math.Min(pos.Y, Bottom(r)); h = Bottom(r) - y; }
                if (_resizeEdge.Value.HasFlag(Edge.B)) h = Math.Max(1, pos.Y - r.Y);

                UpdateRubber(x, y, w, h);
            }
        }

        private void OnPointerReleased(object _, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_anchor is null) return;
            _root.ReleasePointerCapture(e.Pointer);
            _anchor = null;

            if (_mode == Mode.Drawing && _rubber.Width < 4)
            {
                _rubber.Visibility = Visibility.Collapsed;
                return;
            }

            _mode = Mode.Idle;
            UpdateHoverCursor(e.GetCurrentPoint(_root).Position);
            ShowFlyout();
        }
        #endregion

        #region rubber & dimming
        private void CoverEverything()
        {
            /* size the default overlay */
            SetRect(_overlay, 0, 0, _root.ActualWidth, _root.ActualHeight);

            /* collapse band rectangles until a selection exists */
            _dimT.Width = _dimT.Height =
            _dimL.Width = _dimL.Height =
            _dimR.Width = _dimR.Height =
            _dimB.Width = _dimB.Height = 0;
        }

        private void UpdateRubber(double x, double y, double w, double h)
        {
            Canvas.SetLeft(_rubber, x); Canvas.SetTop(_rubber, y);
            _rubber.Width = w; _rubber.Height = h;
            _rubber.Visibility = Visibility.Visible;

            _rect = new RectInt32((int)Math.Round(x), (int)Math.Round(y),
                                  (int)Math.Round(w), (int)Math.Round(h));

            UpdateDimRects();
        }

        private void UpdateDimRects()
        {
            if (_rect is not RectInt32 r) { CoverEverything(); return; }

            _overlay.Visibility = Visibility.Collapsed;

            double W = _root.ActualWidth, H = _root.ActualHeight;
            SetRect(_dimT, 0, 0, W, r.Y);
            SetRect(_dimL, 0, r.Y, r.X, r.Height);
            SetRect(_dimR, Right(r), r.Y, W - Right(r), r.Height);
            SetRect(_dimB, 0, Bottom(r), W, H - Bottom(r));
        }

        private static void SetRect(Rectangle rc, double x, double y, double w, double h)
        {
            Canvas.SetLeft(rc, x); Canvas.SetTop(rc, y);
            rc.Width = Math.Max(0, w);
            rc.Height = Math.Max(0, h);
        }
        #endregion

        #region fly-out
        private CommandBarFlyout BuildFlyout()
        {
            var ok = new AppBarButton 
            { 
                Icon = new SymbolIcon(Symbol.Accept)
            };
            
            var cancel = new AppBarButton 
            { 
                Icon = new SymbolIcon(Symbol.Cancel)
            };

            // Apply theme-aware colors for visibility
            try
            {
                // Use system accent color for the OK button
                if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var accentColor))
                {
                    ok.Background = new SolidColorBrush((Windows.UI.Color)accentColor);
                    ok.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255,255,255,255));
                }
                else
                {
                    // Fallback accent color
                    ok.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 194, 255));
                    ok.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255,255,255,255));
                }

                // Use red color for cancel button
                cancel.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28));
            }
            catch
            {
                // Ultimate fallback - ensure buttons are visible
                ok.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 194, 255));
                ok.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255,255,255,255));
                cancel.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28));
            }

            var f = new CommandBarFlyout
            {
                Placement = FlyoutPlacementMode.Top,
                AlwaysExpanded = true
            };
            
            f.PrimaryCommands.Add(ok);
            f.PrimaryCommands.Add(cancel);

            ok.Click += (_, _) => AcceptAndClose();
            cancel.Click += (_, _) => { _rect = null; Close(); };
            return f;
        }

        private void ShowFlyout()
        {
            if (_rect is not RectInt32 r) return;

            var pos = new Windows.Foundation.Point(r.X + r.Width / 2,
                                                   Math.Max(4, r.Y - 8));

            _flyout.ShowAt(_root, new FlyoutShowOptions
            {
                ShowMode = FlyoutShowMode.Transient,
                Position = pos,
                Placement = FlyoutPlacementMode.Top
            });
        }

        private void AcceptAndClose() => Close();
        #endregion

        #region cursor helpers
        private void UpdateHoverCursor(Windows.Foundation.Point? pos)
        {
            var shape = InputSystemCursorShape.Arrow;

            if (pos.HasValue && _rect.HasValue)
            {
                var edge = HitEdge(pos.Value);
                shape = edge switch
                {
                    Edge.L or Edge.R => InputSystemCursorShape.SizeWestEast,
                    Edge.T or Edge.B => InputSystemCursorShape.SizeNorthSouth,
                    Edge.L | Edge.T => InputSystemCursorShape.SizeNorthwestSoutheast,
                    Edge.R | Edge.B => InputSystemCursorShape.SizeNorthwestSoutheast,
                    Edge.L | Edge.B => InputSystemCursorShape.SizeNortheastSouthwest,
                    Edge.R | Edge.T => InputSystemCursorShape.SizeNortheastSouthwest,
                    null when InsideRect(pos.Value, _rect.Value) => InputSystemCursorShape.SizeAll,
                    _ => InputSystemCursorShape.Cross
                };
            }
            _root.SetCursor(InputSystemCursor.Create(shape));
        }
        #endregion

        #region geometry helpers
        private Edge? HitEdge(Windows.Foundation.Point p)
        {
            if (_rect is not RectInt32 r) return null;
            
            // Check if point is within the extended bounds of the rectangle (including hit tolerance)
            if (p.X < r.X - HIT || p.X > Right(r) + HIT || 
                p.Y < r.Y - HIT || p.Y > Bottom(r) + HIT)
                return null;
            
            bool L = Math.Abs(p.X - r.X) <= HIT;
            bool R = Math.Abs(p.X - Right(r)) <= HIT;
            bool T = Math.Abs(p.Y - r.Y) <= HIT;
            bool B = Math.Abs(p.Y - Bottom(r)) <= HIT;
            if (!(L || R || T || B)) return null;

            Edge e = 0; if (L) e |= Edge.L; if (R) e |= Edge.R;
            if (T) e |= Edge.T; if (B) e |= Edge.B;
            return e;
        }
        private static bool InsideRect(Windows.Foundation.Point p, RectInt32 r) =>
            p.X >= r.X && p.X <= Right(r) && p.Y >= r.Y && p.Y <= Bottom(r);
        private static int Right(RectInt32 r) => r.X + r.Width;
        private static int Bottom(RectInt32 r) => r.Y + r.Height;
        #endregion

        #region HWND styles & positioning
        private static void InitWindowStyles(IntPtr hwnd)
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_TOPMOST = 0x00000008;
            const int WS_POPUP = unchecked((int)0x80000000);
            const int GWL_EXSTYLE = -20, GWL_STYLE = -16;

            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            ex |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, ex);
            SetWindowLong(hwnd, GWL_STYLE, WS_POPUP);

            if (Environment.OSVersion.Version.Build >= 22000)
            {
                const int DWMWA_WINDOW_CORNER_PREFERENCE = 33; uint DONOTROUND = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE,
                                      ref DONOTROUND, sizeof(uint));
            }
        }

        private static void StretchOverDesktop(IntPtr hwnd)
        {
            int x = GetSystemMetrics(76), y = GetSystemMetrics(77);
            int w = GetSystemMetrics(78), h = GetSystemMetrics(79);
            const uint SWP_SHOWWINDOW = 0x0040,
                       SWP_FRAMECHANGED = 0x0020;
            SetWindowPos(hwnd, new IntPtr(-1), x, y, w, h,
                         SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        }
        #endregion

        #region P/Invoke
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int n);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int n, int v);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hInsert,
                                                int X, int Y, int CX, int CY, uint flags);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd,
                                                         int attr, ref uint value, int size);
        #endregion
    }
}