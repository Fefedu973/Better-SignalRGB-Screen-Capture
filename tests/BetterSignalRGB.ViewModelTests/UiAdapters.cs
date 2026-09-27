// Only UI dispatch and the bitmap type are replaced. All view-model
// partials, generated Toolkit commands, models and geometry are production files.
namespace Microsoft.UI.Xaml.Media.Imaging { public sealed class BitmapImage { } }

namespace Better_SignalRGB_Screen_Capture
{
    internal static class App
    {
        public static TestWindow MainWindow { get; } = new();
    }
    internal sealed class TestWindow
    {
        public TestDispatcher DispatcherQueue { get; } = new();
    }
    internal sealed class TestDispatcher
    {
        public bool TryEnqueue(Action action) { action(); return true; }
    }
}
