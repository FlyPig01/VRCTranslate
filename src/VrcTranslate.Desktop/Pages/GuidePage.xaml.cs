using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VrcTranslate.Desktop.Pages;

public sealed partial class GuidePage : Page
{
    public GuidePage()
    {
        InitializeComponent();
        // The shortcut table is a promise about what the keys do, so it lists
        // what the poller actually answers: a cleared shortcut reads 未设置
        // instead of the shipped default it no longer reacts to. Applying it in
        // the constructor keeps the first paint correct; Loaded re-reads it in
        // case the frame ever caches the page.
        ApplyPersistedHotkeys();
        Loaded += (_, _) => ApplyPersistedHotkeys();
    }

    private void ApplyPersistedHotkeys()
    {
        GuideQuickInputHotkey.Text = HotkeyDisplay.Format(HotkeyDisplay.ReadQuickInput());
        GuideOtherPlayerVoiceHotkey.Text = HotkeyDisplay.Format(HotkeyDisplay.ReadOtherPlayerVoice());
        GuideSelfVoiceHotkey.Text = HotkeyDisplay.Format(HotkeyDisplay.ReadSelfVoice());
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ContentColumn is null) return;
        ContentColumn.Width = Math.Max(0, Math.Min(900, e.NewSize.Width - 64));
    }
}
