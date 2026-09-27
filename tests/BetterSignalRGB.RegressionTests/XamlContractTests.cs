using System.Xml.Linq;

namespace BetterSignalRGB.RegressionTests;

internal static class XamlContractTests
{
    public static void Run()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace settings = "using:CommunityToolkit.WinUI.Controls";
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Xaml"), "*.xaml");
        Assert.True(files.Length > 0, "Production XAML must be available for UI framework contract checks");
        foreach (var file in files)
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                var location = $"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)element).LineNumber}";
                // These elements derive from Panel/FrameworkElement, not Control.
                // Native XAML diagnostics did not reliably report the invalid member.
                if (element.Name.Namespace == presentation && element.Name.LocalName is "StackPanel" or "Grid" or "Canvas" or "Border")
                    Assert.True(element.Attribute("IsEnabled") == null, $"{location}: IsEnabled belongs on a Control or on the interactive children");

                // SettingsExpander applies a SettingsCard style at runtime; inserting a
                // UserControl directly compiles but crashes when the expander measures.
                if (element.Name == settings + "SettingsExpander.Items")
                    foreach (var child in element.Elements())
                        Assert.True(child.Name == settings + "SettingsCard", $"{location}: direct expander items must be SettingsCard containers");

                if (element.Attribute("Canvas.ZIndex") is { } zIndex && int.TryParse(zIndex.Value, out var value))
                    Assert.True(value <= 1_000_000, $"{location}: Canvas.ZIndex exceeds WinUI's supported maximum");

                // Storyboards are resolved only when their visual state is entered.
                // A Border named ContentPresenter compiled successfully, but crashed
                // when the recording command disabled its button and animated Foreground.
                if (element.Attribute("Storyboard.TargetName") is { } targetName &&
                    element.Attribute("Storyboard.TargetProperty") is { } property)
                {
                    var scope = element.Ancestors(presentation + "ControlTemplate").FirstOrDefault() ?? document.Root!;
                    var target = scope.Descendants().SingleOrDefault(candidate =>
                        candidate.Attribute(xaml + "Name")?.Value == targetName.Value &&
                        (candidate.Ancestors(presentation + "ControlTemplate").FirstOrDefault() ?? document.Root!) == scope);
                    Assert.True(target != null, $"{location}: storyboard target must resolve in its own XAML namescope");
                    if (target?.Name.Namespace == presentation && target.Name.LocalName is "Border" or "Grid" or "Canvas" or "StackPanel")
                        Assert.True(property.Value is not "Foreground" and not "IsEnabled",
                            $"{location}: {target.Name.LocalName} cannot animate {property.Value}");
                }
            }
        }
    }
}
