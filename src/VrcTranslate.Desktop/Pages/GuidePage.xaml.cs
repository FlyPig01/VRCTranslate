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
        ApplyAuthorShop();
        Loaded += (_, _) => ApplyPersistedHotkeys();
    }

    /// <summary>
    /// 作者小店是用户自己的配置项：拿到合法链接才显示卡片，否则整张折叠。
    /// 每次进页面都重读，用户改了 json 不必重启软件。
    /// </summary>
    private void ApplyAuthorShop()
    {
        var url = AuthorShop.ReadUrl();
        if (url.Length == 0)
        {
            ShopCard.Visibility = Visibility.Collapsed;
            return;
        }

        ShopDescription.Text = AuthorShop.Description;
        ShopLink.Content = "在闲鱼打开作者的小店";
        ShopLink.NavigateUri = new Uri(url);
        ShopCard.Visibility = Visibility.Visible;
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
