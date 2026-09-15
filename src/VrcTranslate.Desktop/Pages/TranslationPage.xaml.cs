using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VrcTranslate.Infrastructure.Storage;
using Microsoft.UI.Text;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Infrastructure.Configuration;
using VrcTranslate.Infrastructure.Translation;

namespace VrcTranslate.Desktop.Pages;

public sealed partial class TranslationPage : Page
{
    private readonly JsonConfigurationStore<GlossaryDocument> _glossaryStore;
    private readonly ObservableCollection<GlossaryEntry> _terms = [];
    private bool _loaded;
    private bool _glossaryAutoSaveReady;
    private AppState State => ((App)Microsoft.UI.Xaml.Application.Current).State;

    private static readonly IReadOnlyDictionary<string, string> ProviderNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["echo"] = "本地测试",
        ["deepseek"] = "DeepSeek",
        ["xiaomi"] = "小米 MiMo",
        ["tencent"] = "腾讯云翻译",
        ["aliyun"] = "阿里云机器翻译"
    };

    public TranslationPage()
    {
        InitializeComponent();
        _glossaryStore = new JsonConfigurationStore<GlossaryDocument>(PortableStorage.GetPath(AppDataFiles.Glossary));
        GlossaryList.ItemsSource = _terms;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        State.RouteChanged += OnRouteChanged;
        // Profiles are restored asynchronously. Build the cards only after the
        // restore completes so the first frame cannot show a stale/default list.
        try { await State.Ready; } catch { }
        RebuildProfiles();
        try
        {
            var bundledTerms = await LoadDefaultTermsAsync();
            var document = await _glossaryStore.LoadAsync();
            var merged = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var term in bundledTerms) merged[term.Source] = term;
            foreach (var term in document.Entries ?? [])
                if (!string.IsNullOrWhiteSpace(term.Source) && !string.IsNullOrWhiteSpace(term.Target))
                    merged[term.Source.Trim()] = new GlossaryEntry(term.Source.Trim(), term.Target.Trim());
            _terms.Clear();
            foreach (var term in merged.Values) _terms.Add(term);
            GlossaryEnabledSwitch.IsOn = document.Enabled;
        }
        catch { }
        if (_terms.Count == 0)
        {
            foreach (var term in await LoadDefaultTermsAsync()) _terms.Add(term);
            if (_terms.Count == 0)
                foreach (var term in DefaultTerms) _terms.Add(term);
        }
        UpdateGlossaryEmptyState();
        // From here on every list change writes the document: the card has no
        // save button, so the file must always match what is on screen.
        _glossaryAutoSaveReady = true;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => State.RouteChanged -= OnRouteChanged;

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ContentColumn is null) return;
        ContentColumn.Width = Math.Max(0, Math.Min(960, e.NewSize.Width - 48));
    }

    private void OnRouteChanged(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(RebuildProfiles); return; }
        RebuildProfiles();
    }

    private void RebuildProfiles()
    {
        if (ProfilesPanel is null) return;
        ProfilesPanel.Children.Clear();
        var profiles = State.TranslationProfiles ?? [];
        if (profiles.Count == 0 && State.DefaultProfile is not null) profiles = [State.DefaultProfile];
        foreach (var profile in profiles) ProfilesPanel.Children.Add(CreateProfileCard(profile));
        if (ProfilesPanel.Children.Count == 0)
            ProfilesPanel.Children.Add(new TextBlock { Text = "暂无服务", Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextStyle"] });
    }

    private Border CreateProfileCard(TranslationProfileRecord profile)
    {
        var current = IsCurrentProfile(profile);
        var title = ProviderNames.TryGetValue(profile.Provider, out var providerName) ? providerName : profile.Provider;
        var model = string.IsNullOrWhiteSpace(profile.Model) ? "默认模型" : profile.Model;
        var details = $"{title} · {model}" + (current ? " · 当前使用" : string.Empty);
        var name = new TextBlock { Text = profile.DisplayName, FontWeight = FontWeights.SemiBold, FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Stretch };
        var detail = new TextBlock { Text = details, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextStyle"], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Stretch };
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, Children = { name, detail } };
        var edit = new Button { Content = "编辑", Tag = profile.Id, Width = 64, MinWidth = 64, Padding = new Thickness(8, 0, 8, 0) };
        AutomationProperties.SetName(edit, $"编辑 {profile.DisplayName}");
        AutomationProperties.SetAutomationId(edit, $"edit-{profile.Provider}");
        edit.Click += OnEditServiceClicked;
        var remove = new Button { Content = "删除", Tag = profile.Id, Width = 64, MinWidth = 64, Padding = new Thickness(8, 0, 8, 0) };
        AutomationProperties.SetName(remove, $"删除 {profile.DisplayName}");
        AutomationProperties.SetAutomationId(remove, $"delete-{profile.Provider}");
        remove.Click += OnDeleteServiceClicked;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { edit, remove } };
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 8, Padding = new Thickness(14, 10, 14, 10), HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(labels); Grid.SetColumn(labels, 0); Grid.SetRow(labels, 0);
        grid.Children.Add(actions); Grid.SetColumn(actions, 1); Grid.SetRow(actions, 0);
        var card = new Border { Tag = profile.Id, Child = grid, IsTapEnabled = true, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(current ? 2 : 1), BorderBrush = new SolidColorBrush(current ? ColorHelper.FromArgb(255, 72, 132, 224) : ColorHelper.FromArgb(255, 215, 226, 240)), Background = new SolidColorBrush(current ? ColorHelper.FromArgb(255, 234, 242, 255) : ColorHelper.FromArgb(255, 245, 248, 252)), MinHeight = 70 };
        var compactApplied = (bool?)null;
        void LayoutCard()
        {
            var width = grid.ActualWidth;
            if (width <= 0) return;
            var compact = width < 560;
            if (compactApplied == compact) return;
            compactApplied = compact;
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            if (compact)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(labels, 0);
                Grid.SetRow(labels, 0);
                Grid.SetColumn(actions, 0);
                Grid.SetRow(actions, 1);
                actions.HorizontalAlignment = HorizontalAlignment.Left;
                actions.Margin = new Thickness(0, 2, 0, 0);
                card.MinHeight = 112;
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(labels, 0);
                Grid.SetRow(labels, 0);
                Grid.SetColumn(actions, 1);
                Grid.SetRow(actions, 0);
                actions.HorizontalAlignment = HorizontalAlignment.Left;
                actions.Margin = new Thickness(0);
                card.MinHeight = 70;
            }
        }
        grid.SizeChanged += (_, _) => LayoutCard();
        card.Loaded += (_, _) => LayoutCard();
        AutomationProperties.SetName(card, current ? $"选择 {profile.DisplayName}（当前）" : $"选择 {profile.DisplayName}");
        card.Tapped += OnProfileCardTapped;
        return card;
    }

    private bool IsCurrentProfile(TranslationProfileRecord profile)
    {
        var routeProfile = State.CurrentRoute.Profile;
        if (string.Equals(routeProfile.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase)) return true;

        // Restored legacy routes can use the synthetic "default-profile" id.
        // Match their concrete route fields before falling back to the first
        // profile, so the highlight still identifies the service in use.
        bool MatchesConcreteRoute(TranslationProfileRecord candidate) =>
            string.Equals(routeProfile.Provider, candidate.Provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(routeProfile.Model, candidate.Model, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(routeProfile.Endpoint.ToString().TrimEnd('/'), candidate.Endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        if (MatchesConcreteRoute(profile)) return true;
        // If an imported route has no corresponding card at all, retain a
        // single fallback highlight instead of leaving the page ambiguous.
        var hasConcreteMatch = State.TranslationProfiles.Any(MatchesConcreteRoute);
        return !hasConcreteMatch && string.Equals(State.DefaultProfile?.Id, profile.Id, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnNewServiceClicked(object sender, RoutedEventArgs e) => await ShowProfileDialogAsync(null);

    private async void OnEditServiceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            await ShowProfileDialogAsync(State.TranslationProfiles?.FirstOrDefault(p => p.Id == id));
    }

    private async void OnDeleteServiceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var profile = State.TranslationProfiles?.FirstOrDefault(p => p.Id == id);
        if (profile is null) return;
        var dialog = new ContentDialog { Title = "删除服务档案", Content = new TextBlock { Text = $"确定删除“{profile.DisplayName}”吗？此操作不会删除其他档案。", TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (State.DeleteProfile(id)) ShowMessage("档案已删除。", InfoBarSeverity.Success);
            else ShowMessage("至少需要保留一个服务档案。", InfoBarSeverity.Warning);
        }
    }

    private async void OnProfileCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is Button || sender is not Border { Tag: string id }) return;
        var record = State.TranslationProfiles?.FirstOrDefault(profile => profile.Id == id);
        // 出厂预设（小米 MiMo / 腾讯云 / 阿里云）还没有密钥时，选中它只会让下一次翻译失败：
        // 直接打开编辑对话框，用户粘贴密钥保存即生效，少绕一步。
        if (record is not null && IsUnconfiguredPreset(record))
        {
            await ShowProfileDialogAsync(record);
            return;
        }

        State.SetDefaultProfile(id);
        ShowMessage("已切换当前翻译服务。", InfoBarSeverity.Success);
    }

    /// <summary>
    /// True while a profile still carries exactly the shipped preset values and no
    /// key of its own - that is, the user has not configured this service yet.
    /// </summary>
    private static bool IsUnconfiguredPreset(TranslationProfileRecord profile) =>
        string.Equals(profile.CredentialReference, "本地配置", StringComparison.Ordinal) &&
        PresetEndpoints.TryGetValue(profile.Provider, out var endpoint) &&
        profile.Endpoint.TrimEnd('/').Equals(endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Endpoints of the shipped presets; a profile still on one of them has no user configuration.</summary>
    private static readonly IReadOnlyDictionary<string, string> PresetEndpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek"] = "https://api.deepseek.com",
        ["xiaomi"] = "https://api.xiaomimimo.com/v1",
        ["tencent"] = "https://tmt.tencentcloudapi.com",
        ["aliyun"] = "https://mt.cn-hangzhou.aliyuncs.com",
        ["echo"] = "https://localhost/echo"
    };

    private async Task ShowProfileDialogAsync(TranslationProfileRecord? existing)
    {
        var isNew = existing is null;
        var nameBox = new TextBox { Header = "档案名称", Text = existing?.DisplayName ?? "新的翻译服务", PlaceholderText = "例如：日常中文翻译" };
        var providerBox = new ComboBox { Header = "服务类型", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var pair in ProviderNames) providerBox.Items.Add(new ComboBoxItem { Content = pair.Value, Tag = pair.Key });
        SelectByTag(providerBox, existing?.Provider ?? "deepseek");
        var modelBox = new TextBox { Header = "模型或接口版本", Text = existing?.Model ?? string.Empty, PlaceholderText = "填写服务商提供的模型或版本" };
        var endpointBox = new TextBox { Header = "接口地址", Text = existing?.Endpoint ?? "", PlaceholderText = "https://api.example.com/v1" };
        var credentialBox = new PasswordBox { Header = "API 密钥", Password = existing?.CredentialReference == "本地配置" ? string.Empty : existing?.CredentialReference ?? string.Empty, PlaceholderText = "留空表示使用已保存凭据" };
        var secretBox = new PasswordBox { Header = "SecretKey / AccessKey Secret（可选）", Password = existing?.Options?.GetValueOrDefault("secret") ?? string.Empty };
        var regionBox = new TextBox { Header = "区域（可选）", Text = existing?.Region ?? string.Empty, PlaceholderText = "例如 ap-guangzhou" };
        var lastProvider = existing?.Provider ?? "deepseek";
        void RefreshProviderFields()
        {
            var id = (providerBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
            var defaults = id switch
            {
                "echo" => ("本地回显", "https://localhost/echo", "无需密钥"),
                "deepseek" => ("deepseek-flash", "https://api.deepseek.com", "DeepSeek API Key"),
                "xiaomi" => ("mimo-v2.5", "https://api.xiaomimimo.com/v1", "小米 MiMo API Key（sk- 开头）"),
                "tencent" => ("TextTranslate", "https://tmt.tencentcloudapi.com", "腾讯云 SecretId"),
                "aliyun" => ("general", "https://mt.cn-hangzhou.aliyuncs.com", "阿里云 AccessKey ID"),
                _ => ("deepseek-flash", "https://api.deepseek.com", "DeepSeek API Key")
            };
            modelBox.Header = id is "echo" or "tencent" or "aliyun" ? "接口版本" : "模型名称";
            modelBox.PlaceholderText = defaults.Item1;
            endpointBox.PlaceholderText = defaults.Item2;
            credentialBox.Header = defaults.Item3;
            endpointBox.Header = id is "echo" ? "服务地址" : "接口地址";
            if (string.IsNullOrWhiteSpace(modelBox.Text)) modelBox.Text = defaults.Item1;
            if (string.IsNullOrWhiteSpace(endpointBox.Text)) endpointBox.Text = defaults.Item2;
            secretBox.Visibility = id is "tencent" or "aliyun" ? Visibility.Visible : Visibility.Collapsed;
            regionBox.Visibility = id is "tencent" or "aliyun" ? Visibility.Visible : Visibility.Collapsed;
            secretBox.Header = id switch
            {
                "tencent" => "腾讯云 SecretKey",
                "aliyun" => "阿里云 AccessKey Secret",
                _ => "附加密钥（可选）"
            };
        }
        providerBox.SelectionChanged += (_, _) =>
        {
            var selectedProvider = (providerBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
            if (!string.Equals(selectedProvider, lastProvider, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(modelBox.Text) || modelBox.Text is "gpt-4.1-mini" or "deepseek-chat" or "deepseek-flash" or "mimo-v2.5" or "本地回显" or "TextTranslate" or "general") modelBox.Text = string.Empty;
                if (endpointBox.Text is "https://api.deepseek.com" or "https://api.openai.com/v1" or "https://api.xiaomimimo.com/v1" or "https://localhost/echo" or "https://tmt.tencentcloudapi.com" or "https://mt.cn-hangzhou.aliyuncs.com") endpointBox.Text = string.Empty;
                lastProvider = selectedProvider;
            }
            RefreshProviderFields();
        };
        RefreshProviderFields();
        // 密钥获取引导只留一句指向指南页：控制台直达与额度要点都在那里。
        var guideHint = new TextBlock
        {
            Text = "需要密钥？见「指南 → 翻译服务配置」。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 96, 112, 131)),
            TextWrapping = TextWrapping.Wrap
        };
        var testButton = new Button { Content = "测试连接", MinWidth = 104 };
        AutomationProperties.SetName(testButton, "测试连接");
        var testNote = new TextBlock
        {
            Text = "测试会向该服务发一句测试文本（会计入你的用量）；不测试也能保存。",
            Style = (Style)global::Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        var testStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Visibility = Visibility.Collapsed
        };
        void ShowTestResult(string text, bool? outcome = null)
        {
            testStatus.Text = text;
            testStatus.Foreground = new SolidColorBrush(outcome switch
            {
                true => Microsoft.UI.ColorHelper.FromArgb(255, 33, 134, 96),
                false => Microsoft.UI.ColorHelper.FromArgb(255, 197, 84, 48),
                _ => Microsoft.UI.ColorHelper.FromArgb(255, 96, 112, 131)
            });
            testStatus.Visibility = Visibility.Visible;
        }
        testButton.Click += async (_, _) =>
        {
            var id = (providerBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                ShowTestResult("请先选择服务类型。");
                return;
            }

            // 留空的密钥框表示沿用已保存凭据，与保存路径同义。
            var credential = !string.IsNullOrWhiteSpace(credentialBox.Password)
                ? credentialBox.Password.Trim()
                : existing?.CredentialReference is { Length: > 0 } saved &&
                  saved != "本地配置" &&
                  !saved.StartsWith("vault:", StringComparison.OrdinalIgnoreCase) &&
                  !saved.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase)
                    ? saved
                    : null;
            var secret = !string.IsNullOrWhiteSpace(secretBox.Password)
                ? secretBox.Password.Trim()
                : existing?.Options?.GetValueOrDefault("secret");
            if (id != "echo" && credential is null)
            {
                ShowTestResult("请先填写密钥再测试。");
                return;
            }
            if (id is "tencent" or "aliyun" && string.IsNullOrWhiteSpace(secret))
            {
                ShowTestResult("请先填写 SecretKey / AccessKey Secret 再测试。");
                return;
            }

            var endpointText = string.IsNullOrWhiteSpace(endpointBox.Text)
                ? PresetEndpoints.GetValueOrDefault(id) ?? endpointBox.Text.Trim()
                : endpointBox.Text.Trim();
            if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
            {
                ShowTestResult("接口地址无效，请检查后重试。");
                return;
            }

            testButton.IsEnabled = false;
            testStatus.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 96, 112, 131));
            testStatus.Text = "正在测试…";
            testStatus.Visibility = Visibility.Visible;
            try
            {
                var model = string.IsNullOrWhiteSpace(modelBox.Text)
                    ? id switch
                    {
                        "deepseek" => "deepseek-flash",
                        "xiaomi" => "mimo-v2.5",
                        "tencent" => "TextTranslate",
                        "aliyun" => "general",
                        _ => "本地回显"
                    }
                    : modelBox.Text.Trim();
                var options = new Dictionary<string, string>();
                if (!string.IsNullOrWhiteSpace(secret)) options["secret"] = secret;
                var request = new TranslationProviderRequest(
                    "你好", "en", "zh-CN", model, endpoint, credential ?? "本地配置",
                    Guid.NewGuid().ToString("N"), id, regionBox.Text.Trim(), options);
                var resolved = TranslationProviderRegistry.TryResolveId(id) ?? id;
                var provider = TranslationProviderRegistry
                    .CreateShippedProviders()
                    .FirstOrDefault(candidate => string.Equals(candidate.Id, resolved, StringComparison.Ordinal));
                if (provider is null)
                {
                    ShowTestResult("找不到该服务的适配器。");
                    return;
                }

                var result = await TranslationConnectionTester.TestAsync(provider, request);
                if (result.Succeeded)
                {
                    ShowTestResult($"✓ 连接正常，翻译服务可用（返回：{result.Translation}）。", true);
                }
                else
                {
                    ShowTestResult($"{result.Hint ?? "测试失败。"}\n{result.Detail}", false);
                }
            }
            finally
            {
                testButton.IsEnabled = true;
            }
        };
        var stack = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                nameBox, providerBox, modelBox, endpointBox, credentialBox, secretBox, regionBox,
                guideHint,
                new StackPanel { Spacing = 6, Children = { testButton, testNote, testStatus } }
            }
        };
        var dialog = new ContentDialog
        {
            Title = isNew ? "新增翻译服务档案" : "编辑翻译服务档案",
            Content = new ScrollViewer
            {
                Content = stack,
                MaxHeight = 560,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            },
            PrimaryButtonText = "保存并设为当前",
            SecondaryButtonText = "仅保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result is not (ContentDialogResult.Primary or ContentDialogResult.Secondary)) return;
        var provider = (providerBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "echo";
        var options = new Dictionary<string, string>(); if (!string.IsNullOrWhiteSpace(secretBox.Password)) options["secret"] = secretBox.Password;
        var record = new TranslationProfileRecord(
            existing?.Id ?? Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(nameBox.Text) ? "新的翻译服务" : nameBox.Text.Trim(),
            provider,
            modelBox.Text.Trim(),
            endpointBox.Text.Trim(),
            string.IsNullOrWhiteSpace(credentialBox.Password) ? "本地配置" : credentialBox.Password,
            existing?.SourceLanguage ?? "auto",
            existing?.TargetLanguage ?? "zh-CN",
            regionBox.Text.Trim(),
            options);
        try { State.SaveProfile(record, result == ContentDialogResult.Primary); ShowMessage("档案已保存。", InfoBarSeverity.Success); } catch (Exception ex) { ShowMessage($"保存失败：{ex.Message}", InfoBarSeverity.Error); }
    }

    private void OnAddTermClicked(object sender, RoutedEventArgs e)
    {
        var source = TermSourceBox.Text.Trim(); var target = TermTargetBox.Text.Trim();
        if (source.Length == 0 || target.Length == 0) { ShowGlossaryMessage("请填写原词和译词。", InfoBarSeverity.Warning); return; }
        var existing = _terms.FirstOrDefault(x => string.Equals(x.Source, source, StringComparison.OrdinalIgnoreCase)); if (existing is not null) _terms[_terms.IndexOf(existing)] = new GlossaryEntry(source, target); else _terms.Add(new GlossaryEntry(source, target)); TermSourceBox.Text = string.Empty; TermTargetBox.Text = string.Empty; UpdateGlossaryEmptyState();
        _ = PersistGlossaryAsync();
    }

    private void OnRemoveTermClicked(object sender, RoutedEventArgs e) { if (sender is Button { Tag: GlossaryEntry term }) _terms.Remove(term); UpdateGlossaryEmptyState(); _ = PersistGlossaryAsync(); }

    /// <summary>Enabling or disabling the glossary is part of the same document and is persisted too.</summary>
    private void OnGlossaryEnabledToggled(object sender, RoutedEventArgs e)
    {
        // Restoring the saved document sets the switch; that pass must not write it back.
        if (!_glossaryAutoSaveReady) return;
        _ = PersistGlossaryAsync();
    }

    /// <summary>
    /// The glossary card has no save button, so every add, edit, removal and
    /// enable change writes the whole document immediately. Without this the
    /// list would look saved while glossary.json stayed stale.
    /// </summary>
    private async Task PersistGlossaryAsync()
    {
        try
        {
            await _glossaryStore.SaveAsync(new GlossaryDocument { Enabled = GlossaryEnabledSwitch.IsOn, Entries = _terms.ToList() });
            ShowGlossaryMessage("术语库已自动保存。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowGlossaryMessage($"保存失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void UpdateGlossaryEmptyState() => GlossaryEmptyText.Visibility = _terms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    private void ShowGlossaryMessage(string message, InfoBarSeverity severity) { GlossaryInfo.Title = severity == InfoBarSeverity.Success ? "已完成" : "请检查"; GlossaryInfo.Message = message; GlossaryInfo.Severity = severity; GlossaryInfo.IsOpen = true; }
    private void ShowMessage(string message, InfoBarSeverity severity) { ResultInfo.Title = severity == InfoBarSeverity.Success ? "已完成" : "请检查"; ResultInfo.Message = message; ResultInfo.Severity = severity; ResultInfo.IsOpen = true; RebuildProfiles(); }
    private static void SelectByTag(ComboBox comboBox, string? tag) => comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag?.ToString(), tag));

    private static readonly GlossaryEntry[] DefaultTerms = [
        new("VRChat", "VRChat"), new("Chatbox", "Chatbox"), new("OSC", "OSC"), new("SenseVoice", "SenseVoice"), new("Avatar", "Avatar"), new("World", "World"), new("Booth", "Booth"), new("SDK", "SDK"), new("Unity", "Unity"), new("Udon", "Udon"), new("VRC", "VRC"), new("Quest", "Quest"), new("PCVR", "PCVR"), new("SteamVR", "SteamVR"), new("Modular Avatar", "Modular Avatar")
    ];

    private static async Task<IReadOnlyList<GlossaryEntry>> LoadDefaultTermsAsync()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "default-glossary.json");
            if (!File.Exists(path)) return DefaultTerms;
            await using var stream = File.OpenRead(path);
            var document = await System.Text.Json.JsonSerializer.DeserializeAsync<GlossaryDocument>(stream);
            return document?.Entries?.Where(item => !string.IsNullOrWhiteSpace(item.Source) && !string.IsNullOrWhiteSpace(item.Target)).ToList() ?? DefaultTerms.ToList();
        }
        catch { return DefaultTerms; }
    }
    public sealed record GlossaryEntry(string Source, string Target);
    public sealed class GlossaryDocument { public GlossaryDocument() { } public bool Enabled { get; set; } = true; public List<GlossaryEntry> Entries { get; set; } = []; }
}
