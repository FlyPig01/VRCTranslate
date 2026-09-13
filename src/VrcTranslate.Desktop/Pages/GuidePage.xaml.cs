using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VrcTranslate.Desktop.Pages;

public sealed partial class GuidePage : Page
{
    public GuidePage() => InitializeComponent();

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ContentColumn is null) return;
        ContentColumn.Width = Math.Max(0, Math.Min(900, e.NewSize.Width - 64));
    }
}
