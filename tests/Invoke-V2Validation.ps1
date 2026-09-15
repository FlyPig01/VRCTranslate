$ErrorActionPreference = 'Stop'

$v2Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $v2Root 'VrcTranslate.sln'
$desktopOutput = Join-Path $v2Root 'src\VrcTranslate.Desktop\bin\x64\Debug\net10.0-windows10.0.19041.0'
$executable = Join-Path $desktopOutput 'VrcTranslate.exe'
$icon = Join-Path $desktopOutput 'app.ico'
$logo = Join-Path $desktopOutput 'logo-mark.png'
$startupLog = Join-Path $desktopOutput 'data\startup-error.log'
$settingsPage = Join-Path $v2Root 'src\VrcTranslate.Desktop\Pages\SettingsPage.xaml'
$settingsPageCode = Join-Path $v2Root 'src\VrcTranslate.Desktop\Pages\SettingsPage.xaml.cs'
$guidePage = Join-Path $v2Root 'src\VrcTranslate.Desktop\Pages\GuidePage.xaml'
$translationPage = Join-Path $v2Root 'src\VrcTranslate.Desktop\Pages\TranslationPage.xaml'
$translationPageCode = Join-Path $v2Root 'src\VrcTranslate.Desktop\Pages\TranslationPage.xaml.cs'
$runPage = Join-Path $v2Root 'src\VrcTranslate.Desktop\Pages\RunPage.xaml'
$runPageCode = Join-Path $v2Root 'src\VrcTranslate.Desktop\Pages\RunPage.xaml.cs'
$inputPage = Join-Path $v2Root 'src\VrcTranslate.Desktop\Pages\SelfMessagePage.xaml'
$voicePage = Join-Path $v2Root 'src\VrcTranslate.Desktop\Pages\VoicePage.xaml'
$voicePageCode = Join-Path $v2Root 'src\VrcTranslate.Desktop\Pages\VoicePage.xaml.cs'
$overlayPage = Join-Path $v2Root 'src\VrcTranslate.Desktop\Pages\SubtitleOverlayWindow.xaml'
$defaultGlossary = Join-Path $v2Root 'src\VrcTranslate.Desktop\Resources\default-glossary.json'
$mainWindow = Join-Path $v2Root 'src\VrcTranslate.Desktop\MainWindow.xaml'
$mainWindowCode = Join-Path $v2Root 'src\VrcTranslate.Desktop\MainWindow.xaml.cs'
$overlayHost = Join-Path $v2Root 'src\VrcTranslate.Desktop\Pages\OverlayWindowHost.cs'
$hotkeyContracts = Join-Path $v2Root 'src\VrcTranslate.Core\Settings\HotkeyContracts.cs'
$settingsValidation = Join-Path $v2Root 'src\VrcTranslate.Core\Settings\WorkspaceSettingsValidation.cs'
$desktopProject = Join-Path $v2Root 'src\VrcTranslate.Desktop\VrcTranslate.Desktop.csproj'
$coreSource = Join-Path $v2Root 'src\VrcTranslate.Core'
$desktopSource = Join-Path $v2Root 'src\VrcTranslate.Desktop'

# A previous failed WinUI launch can leave a zero-handle process record in the
# session. It cannot lock files and is ignored; real processes always have
# handles and are still treated as a validation failure.
$existingProcesses = @()
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    $existingProcesses = @(Get-Process -Name VrcTranslate -ErrorAction SilentlyContinue | Where-Object { $_.HandleCount -gt 0 })
    if ($existingProcesses.Count -eq 0) { break }
    Start-Sleep -Milliseconds 250
}
if ($existingProcesses.Count -gt 0) {
    throw 'Close all running VrcTranslate.exe processes before validation so output files are not locked.'
}

Write-Host '[1/5] Building V2 solution'
dotnet build $solution -c Debug -p:Platform=x64 --no-restore
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

Write-Host '[2/5] Running all automated tests'
dotnet test $solution -c Debug -p:Platform=x64 --no-restore
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }

if (-not (Test-Path $executable)) { throw "Desktop executable was not produced: $executable" }
if (-not (Test-Path $icon)) { throw "Desktop icon was not produced: $icon" }
if ((Get-Item $icon).Length -lt 4000) { throw 'The desktop icon is unexpectedly small.' }
if (-not (Test-Path $logo) -or (Get-Item $logo).Length -lt 500) { throw 'The visible sidebar brand image was not produced.' }
if (-not (Test-Path $defaultGlossary) -or ([regex]::Matches((Get-Content -Raw $defaultGlossary), '"Source"').Count -ne 52)) { throw 'The bundled default glossary must contain exactly 52 default terms.' }
if ((Get-Content -Raw $settingsPage) -match '开机自动启动|StartWithWindows') { throw 'Startup-with-Windows setting must not be present.' }
if ((Get-Content -Raw $guidePage) -notmatch '(?s)QQ.*Ctrl\+Alt\+F') { throw 'The guide must retain the QQ Ctrl+Alt+F OCR note.' }
$translationMarkup = Get-Content -Raw $translationPage
$translationCode = Get-Content -Raw $translationPageCode
$appStateSource = Get-Content -Raw (Join-Path $desktopSource 'AppState.cs')
$sessionHostSource = Get-Content -Raw (Join-Path $desktopSource 'VoiceSessionHost.cs')
$infrastructureSource = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Infrastructure\Translation\DeepSeekTranslationProvider.cs')
$settingsMarkup = Get-Content -Raw $settingsPage
$settingsSource = Get-Content -Raw $settingsPageCode
$mainWindowMarkup = Get-Content -Raw $mainWindow
$mainWindowSource = Get-Content -Raw $mainWindowCode
$overlayHostSource = Get-Content -Raw $overlayHost
$hotkeyContractsSource = Get-Content -Raw $hotkeyContracts
$settingsValidationSource = Get-Content -Raw $settingsValidation
$quickInputSource = Get-Content -Raw (Join-Path $desktopSource 'Pages\QuickInputWindow.cs')
$outputFormatterSource = Get-Content -Raw (Join-Path $coreSource 'Translation\TranslationOutputFormatter.cs')
$selfMessageSource = Get-Content -Raw (Join-Path $desktopSource 'Pages\SelfMessagePage.xaml.cs')
$overlayControllerPath = Join-Path $desktopSource 'Pages\OverlayWindowController.cs'
$overlayControllerSource = Get-Content -Raw $overlayControllerPath
$runMarkup = Get-Content -Raw $runPage
$runSource = Get-Content -Raw $runPageCode
$inputMarkup = Get-Content -Raw $inputPage
$voiceMarkup = Get-Content -Raw $voicePage
$voiceSource = Get-Content -Raw $voicePageCode
$overlayMarkup = Get-Content -Raw $overlayPage
$overlayCodeSource = Get-Content -Raw (Join-Path $desktopSource 'Pages\SubtitleOverlayWindow.xaml.cs')
$overlayLayoutContract = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Core\Settings\OverlayWindowLayout.cs')
$overlayLayoutStore = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Infrastructure\Configuration\OverlayWindowLayoutStore.cs')
$captionBufferSource = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Application\Subtitles\SubtitleCaptionBuffer.cs')
$captionSettingsSource = Get-Content -Raw (Join-Path $desktopSource 'SubtitleCaptionSettings.cs')

function Resolve-OverlayDefaultDimension {
    param(
        [Parameter(Mandatory)]
        [string] $Source,
        [Parameter(Mandatory)]
        [string] $Token,
        [Parameter(Mandatory)]
        [string] $Label,
        [Parameter(Mandatory)]
        [string] $Role
    )

    if ($Token -match '^\d+$') { return [int]$Token }
    $field = [regex]::Match(
        $Source,
        ('(?m)^\s*(?:private|internal|public)?\s*const\s+int\s+{0}\s*=\s*(?<value>\d+)\s*;' -f [regex]::Escape($Token)))
    if (-not $field.Success) {
        throw "$Label overlay $Role must be a literal size or a const int declared in the same file (got '$Token')."
    }

    return [int]$field.Groups['value'].Value
}

function Get-ConfiguredOverlaySize {
    param(
        [Parameter(Mandatory)]
        [string] $Source,
        [Parameter(Mandatory)]
        [string] $Label
    )

    # D10：默认宽高可以写成同一文件里的 const int（宽度必须能和高度一样按 DPI 换算），
    # 也可以仍是字面量；两种写法都要求给出一个具体的默认尺寸。
    $match = [regex]::Match(
        $Source,
        '(?s)new\s+OverlayWindowController\s*\(\s*this\s*,\s*"[^"]+"\s*,\s*(?<width>[A-Za-z_][A-Za-z0-9_]*|\d+)\s*,\s*(?<height>[A-Za-z_][A-Za-z0-9_]*|\d+)\s*,')
    if (-not $match.Success) {
        throw "$Label overlay does not pass a concrete default size to the shared native-window controller."
    }

    $width = Resolve-OverlayDefaultDimension -Source $Source -Token $match.Groups['width'].Value -Label $Label -Role 'width'
    $height = Resolve-OverlayDefaultDimension -Source $Source -Token $match.Groups['height'].Value -Label $Label -Role 'height'
    if ($width -lt 240 -or $height -lt 56 -or $width -gt 10000 -or $height -gt 10000) {
        throw "$Label overlay has an invalid configured size: ${width}x${height}."
    }
    return [pscustomobject]@{ Width = $width; Height = $height }
}

$quickOverlayDefaultSize = Get-ConfiguredOverlaySize -Source $quickInputSource -Label 'Quick-input'
$subtitleOverlayDefaultSize = Get-ConfiguredOverlaySize -Source $overlayCodeSource -Label 'Subtitle'

# Keep the enlarged first-run surfaces explicit. These thresholds encode the
# product requirement that the new defaults are roughly twice the cramped
# legacy widths; saved layouts remain user-controlled after first launch.
if ($quickOverlayDefaultSize.Width -lt 1200) {
    throw "Quick-input default width regressed below the enlarged baseline: $($quickOverlayDefaultSize.Width)px."
}

# D10：窗口矩形是物理像素，而 1240x150 描述的是内容（DIP）。全新安装的默认尺寸
# 必须在内容加载后按当前缩放换算一次宽高，否则高 DPI 下同一串数字会得到更小的一块
# 浮窗（曾出现宽度只剩 2/3）。断言"默认尺寸仍被使用"和"宽高都经过换算"。
if ($quickInputSource -notmatch 'new\s+OverlayWindowController\s*\(\s*this\s*,\s*"[^"]+"\s*,\s*DefaultWidthDips\s*,\s*DefaultHeightDips\s*,') {
    throw 'The quick-input default size must be passed through the named DIP constants instead of raw numbers.'
}
if ($quickInputSource -notmatch 'OverlayDisplayScale\.Resolve|OverlayDisplayScale\.ToPhysicalPixels') {
    throw 'The quick-input default size must be converted from DIP to physical pixels at the current display scale.'
}
$quickDefaultFit = [regex]::Match(
    $quickInputSource,
    '(?s)private void FitDefaultSizeToDisplayScale\(\).*?\r?\n    \}')
if (-not $quickDefaultFit.Success -or
    $quickDefaultFit.Value -notmatch 'ToPhysicalPixels\(DefaultWidthDips' -or
    $quickDefaultFit.Value -notmatch 'ToPhysicalPixels\(DefaultHeightDips') {
    throw 'The quick-input fresh-install fit must scale the default width and height together.'
}
if ($quickInputSource -match '_surface\.XamlRoot\?\.RasterizationScale') {
    throw 'Overlay display-scale reading must go through OverlayDisplayScale so both overlays share one conversion.'
}
if ($subtitleOverlayDefaultSize.Width -lt 1400) {
    throw "Subtitle default width regressed below the enlarged baseline: $($subtitleOverlayDefaultSize.Width)px."
}

function Assert-OverlayBounds {
    param(
        [Parameter(Mandatory)]
        $Bounds,
        [Parameter(Mandatory)]
        $ConfiguredSize,
        [Parameter(Mandatory)]
        [string] $Label
    )

    # The saved layout may intentionally differ from the fresh-install size.
    # Validate the shared chrome contract and leave the actual user size free.
    $minimumWidth = 240
    $minimumHeight = 56
    $maximumWidth = [Math]::Max(2400, [int]$ConfiguredSize.Width * 2)
    $maximumHeight = [Math]::Max(600, [int]$ConfiguredSize.Height * 2)
    if ($Bounds.Width -lt $minimumWidth -or $Bounds.Width -gt $maximumWidth -or
        $Bounds.Height -lt $minimumHeight -or $Bounds.Height -gt $maximumHeight -or
        [double]::IsInfinity($Bounds.Width) -or [double]::IsInfinity($Bounds.Height)) {
        throw "$Label overlay has invalid bounds: $($Bounds.Width)x$($Bounds.Height); configured baseline is $($ConfiguredSize.Width)x$($ConfiguredSize.Height)."
    }
}

function Get-XamlDimension {
    param(
        [Parameter(Mandatory)]
        [string] $Markup,
        [Parameter(Mandatory)]
        [ValidateSet('MinWidth', 'MinHeight')]
        [string] $Property,
        [Parameter(Mandatory)]
        [string] $Label
    )

    $match = [regex]::Match($Markup, ('{0}="(?<value>\d+)"' -f $Property))
    if (-not $match.Success) {
        throw "$Label overlay does not declare $Property."
    }
    return [int]$match.Groups['value'].Value
}

$subtitleOverlayMinWidth = Get-XamlDimension -Markup $overlayMarkup -Property MinWidth -Label 'Subtitle'
$subtitleOverlayMinHeight = Get-XamlDimension -Markup $overlayMarkup -Property MinHeight -Label 'Subtitle'
if ($subtitleOverlayMinWidth -lt 240 -or $subtitleOverlayMinHeight -lt 56) {
    throw "Subtitle overlay minimum size is too small: ${subtitleOverlayMinWidth}x${subtitleOverlayMinHeight}."
}

if ($settingsMarkup -notmatch '<Grid\.RowDefinitions>\s*<RowDefinition Height="Auto" />\s*<RowDefinition Height="Auto" />\s*<RowDefinition Height="Auto" />') { throw 'Shortcut settings must define three separate rows so labels cannot overlap.' }

# D11：自身消息发到聊天框的那份文本是否带原文必须可选，并且落在 OSC 那一张卡片里。
$oscToggle = [regex]::Match($settingsMarkup, '(?s)<ToggleSwitch[^>]*osc-include-original-toggle.*?/>')
if (-not $oscToggle.Success -or
    $oscToggle.Value -notmatch 'Toggled="OnOscIncludeOriginalToggled"') {
    throw 'The OSC card must expose a toggle for whether the chatbox message carries the original text.'
}
$oscSaveSource = [regex]::Match($settingsSource, '(?s)private void OnOscIncludeOriginalToggled.*?\n    \}')
if (-not $oscSaveSource.Success -or $oscSaveSource.Value -notmatch 'TrySave') {
    throw 'The chatbox content toggle must persist immediately, like the OSC host and port boxes.'
}
if ($settingsSource -notmatch 'IncludeOriginalInOsc = OscIncludeOriginalToggle\.IsOn' -or
    $settingsSource -notmatch 'OscChatboxSettings\.DefaultIncludeOriginal') {
    throw 'The saved chatbox content choice must come from the toggle and default to the shipped behaviour.'
}
if ($quickInputSource -notmatch 'OscChatboxSettings\.ReadIncludeOriginal\(\)' -or
    $quickInputSource -notmatch 'TranslationOutputFormatter\.FormatForChatbox\(') {
    throw 'The quick-input send path must build its chatbox payload through the shared formatter and the persisted choice.'
}
if ($sessionHostSource -notmatch 'TranslationOutputFormatter\.FormatForChatbox\(' -or
    $sessionHostSource -notmatch 'OscChatboxSettings\.ReadIncludeOriginal\(\)' -or
    $sessionHostSource -match 'FormatForOsc\(result\)') {
    throw 'Own-voice OSC sends must use the same chatbox formatter and preference as the manual input, never the older entry point.'
}
if ($runSource -notmatch 'FeatureGrid\??\.ActualWidth' -or $runSource -notmatch 'LayoutSummary') { throw 'RunPage must derive its responsive layout from the measured content width.' }
if ($runMarkup -notmatch '(?s)<Grid Grid\.Row="1"[^>]*ColumnSpacing="10".*?<Button Grid\.Column="1"[^>]*Content="打开"[^>]*HorizontalAlignment="Right"' -or $runMarkup -match '<Button Grid\.Row="2"[^>]*Content="打开"') {
    throw 'RunPage feature cards must place the open action on the title row and align it to the right edge.'
}
if ($settingsSource -notmatch 'LayoutHotkeys' -or $settingsSource -notmatch 'LayoutOsc' -or $settingsSource -notmatch 'HotkeyGrid\??\.ActualWidth') { throw 'SettingsPage must provide measured-width responsive shortcut and OSC layouts.' }
if ($translationMarkup -match 'TextBlock Text="翻译设置"') { throw 'TranslationPage must not repeat the shell page title.' }
if ($mainWindowSource -match 'typeof\(TranslationPage\),\s*"翻译设置"' -or $mainWindowMarkup -match 'Text="翻译设置"') { throw 'The desktop shell must expose one concise translation navigation title.' }
if ($translationMarkup -match 'HorizontalContentAlignment="Left"') { throw 'TranslationPage must not use a left-only content alignment.' }
if ($translationMarkup -notmatch 'ProfilesPanel|translation-profile-add') { throw 'Translation page must expose a profile list and add action.' }
if ($translationMarkup -notmatch 'HorizontalAlignment="Stretch"\s+MinWidth="0"') { throw 'TranslationPage content root must stretch within the visible viewport.' }
$maxWidths = @([regex]::Matches($translationMarkup, 'MaxWidth="(\d+)"') | ForEach-Object { [int]$_.Groups[1].Value })
if ($maxWidths | Where-Object { $_ -gt 1120 }) { throw 'TranslationPage max width is too large for the supported shell layout.' }
if ($translationMarkup -notmatch 'MaxWidth="960"') { throw 'TranslationPage must use the bounded 960px content column.' }
if ($translationMarkup -notmatch 'ColumnDefinition Width="2\*"') { throw 'TranslationPage profile and glossary grids must use shared flexible columns.' }
if ($translationMarkup -notmatch 'HorizontalContentAlignment="Stretch"') { throw 'TranslationPage rows must stretch to the bounded content column.' }
if ($translationCode -notmatch 'CreateProfileCard|ShowProfileDialogAsync|ContentDialog|SetDefaultProfile|DeleteProfile') { throw 'Translation profiles must support selectable cards and dialog-based add/edit/delete actions.' }
# D8：DeepL、Google（免费接口）与 Google Cloud 已从产品删除。这里既要求保留的服务仍可选，
# 也要求被删掉的服务不再以任何形式回到翻译页或外壳的注册 / 默认值 / 迁移表里（反向断言）。
if ($translationCode -notmatch 'deepseek|xiaomi|tencent|aliyun') { throw 'The translation page must keep offering the shipped services (DeepSeek / 小米 MiMo / 腾讯云 / 阿里云).' }
if ($translationCode -match '(?i)deepl|google') { throw 'The removed DeepL and Google services must not be selectable on the translation page any more.' }
if ($appStateSource -match '"deepl"|"deep-l"|"google-free"|"google_free"|"google-cloud"|"google_cloud"') { throw 'The removed DeepL and Google providers must not be registered, preset or migrated by the shell any more.' }
if (Test-Path -LiteralPath (Join-Path $v2Root 'src\VrcTranslate.Infrastructure\Translation\GoogleTranslationProviders.cs')) { throw 'The Google translation adapters must stay deleted.' }
# 删除不等于忘记：老配置里可能还写着这些 id，提供商目录必须仍认得它们才能优雅降级
# （单元测试 RemovedTranslationProviderTests 覆盖具体行为）。
$providerRegistrySource = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Infrastructure\Translation\TranslationProviderRegistry.cs')
if ($providerRegistrySource -notmatch 'RetiredProviderIds' -or
    $providerRegistrySource -notmatch '"deepl", "google-free", "google-cloud"') {
    throw 'The removed provider ids must stay known as retired, otherwise a saved profile or route cannot degrade to a servable provider.'
}
if ($appStateSource -notmatch 'TranslationProviderRegistry\.CreateShippedProviders' -or
    $appStateSource -notmatch 'TranslationProfileResolver' -or
    $appStateSource -notmatch 'RetainServable' -or
    $appStateSource -notmatch 'CreateFallbackRoute') {
    throw 'A configuration saved while DeepL or Google were still shipped must degrade: profiles are filtered against the shipped services and the route falls back to a profile that can still be served.'
}
if ($translationCode -match 'Header = "原文语言"|Header = "翻译为"|existing\.SourceLanguage|existing\.TargetLanguage') { throw 'Translation profile dialogs must not expose source or target language settings.' }
if ($appStateSource -notmatch 'deepseek".*, "deepseek-flash"' -or $appStateSource -notmatch 'DefaultModelForProvider' -or $appStateSource -notmatch 'new\("tencent".*"auto".*"zh-CN"' -or $translationCode -notmatch '腾讯云 SecretKey' -or $translationCode -notmatch '阿里云 AccessKey Secret') { throw 'Translation profile defaults and provider-specific credential labels must be explicit.' }
# 小米 MiMo：同一个 OpenAI 兼容协议，但必须是自己的档案 + 自己的预设（端点 / 模型 / 密钥标签），
# 且默认用非思考模型——实测 -pro 单句要 2.9-4.9 秒，翻译用不上。
$xiaomiProviderSource = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Infrastructure\Translation\XiaomiTranslationProvider.cs')
if ($appStateSource -notmatch 'xiaomi".*, "mimo-v2\.5"' -or
    $appStateSource -notmatch '"xiaomi" => "mimo-v2\.5"' -or
    $translationCode -notmatch '小米 MiMo' -or
    $translationCode -notmatch 'https://api\.xiaomimimo\.com/v1' -or
    $xiaomiProviderSource -notmatch 'DefaultModel = "mimo-v2\.5"' -or
    $xiaomiProviderSource -notmatch 'DefaultEndpoint = "https://api\.xiaomimimo\.com/v1"') {
    throw 'Xiaomi MiMo must ship as its own profile preset with the non-reasoning model and the platform endpoint.'
}
if ($providerRegistrySource -notmatch 'new XiaomiTranslationProvider\(\)' -or
    $providerRegistrySource -notmatch 'XiaomiTranslationProvider\.ProviderId') {
    throw 'The Xiaomi MiMo provider must be registered in the shipped provider list.'
}
if ($infrastructureSource -notmatch 'DeepSeekTranslationProvider' -or $infrastructureSource -match 'OpenAiCompatibleTranslationProvider') { throw 'DeepSeek must stay a dedicated provider; the generic OpenAI-compatible adapter is retired.' }
# 思考开关与请求体只在共享实现里写一次，DeepSeek 与小米都从那里取（各自只声明默认值/错误文案）。
$chatCompletionsSource = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Infrastructure\Translation\ChatCompletionsTranslation.cs')
if ($chatCompletionsSource -notmatch '"type"\]\s*=\s*"disabled"' -or
    $chatCompletionsSource -notmatch 'BuildChatEndpoint' -or
    $chatCompletionsSource -notmatch 'ReadTranslation' -or
    $infrastructureSource -notmatch 'ChatCompletionsTranslation\.BuildRequest' -or
    $xiaomiProviderSource -notmatch 'ChatCompletionsTranslation\.BuildRequest') {
    throw 'The chat-completions wire format (thinking switch, endpoint, reply parsing) must live in one shared implementation used by both DeepSeek and Xiaomi MiMo.'
}
if ($translationMarkup -notmatch 'ListViewItem|HorizontalContentAlignment="Stretch"') { throw 'Glossary rows must stretch to the table width.' }
if ($translationCode -notmatch 'DefaultTerms' -or $translationCode -notmatch 'if \(_terms\.Count == 0\)') { throw 'The glossary must seed its default terms when no saved terms exist.' }
# D2：术语库卡片删掉「保存」按钮和重复的「启用」文字，标题与开关并回同一条标题行。
# 术语表本来就是自动保存的，所以新增 / 修改 / 删除 / 开关都必须自己立即落盘。
if ($translationMarkup -match 'glossary-save|Content="保存"|Header="启用"') {
    throw 'The glossary card must not keep a save button or a duplicated 启用 label; the switch already shows 开 / 关.'
}
if ($translationCode -match 'OnSaveGlossaryClicked') {
    throw 'The retired glossary save handler must be removed together with its button.'
}
if ($translationCode -notmatch 'PersistGlossaryAsync' -or
    ([regex]::Matches($translationCode, '_ = PersistGlossaryAsync\(\)').Count -lt 3)) {
    throw 'Adding, editing, removing and enabling a glossary term must each persist the document immediately.'
}
if ($translationMarkup -notmatch '(?s)<Grid ColumnSpacing="12" MinWidth="0">\s*<Grid\.ColumnDefinitions>\s*<ColumnDefinition Width="\*" />\s*<ColumnDefinition Width="Auto" />\s*</Grid\.ColumnDefinitions>\s*<TextBlock Text="术语库"[\s\S]{0,600}<ToggleSwitch[^>]*Grid\.Column="1"') {
    throw 'The glossary title and its switch must share one aligned header row like the translation-service card.'
}
if ($translationCode -notmatch 'LayoutCard' -or $translationCode -notmatch 'grid\.ActualWidth' -or $translationCode -notmatch 'HorizontalScrollBarVisibility = ScrollBarVisibility\.Disabled') { throw 'Translation profile cards and dialogs must adapt to compact widths without horizontal clipping.' }
if (($translationCode -notmatch 'CreateProviderPlaceholders|IsConfiguredProfile' -and $appStateSource -notmatch 'CreateProviderPlaceholders|IsConfiguredProfile') -or $appStateSource -notmatch 'Only this offline profile' -or $appStateSource -notmatch 'IsConfiguredRoute') { throw 'Translation profiles must hide unconfigured provider placeholders and keep only the local test profile by default.' }
if ($settingsMarkup -notmatch 'Text="打开输入框"' -or $settingsMarkup -match 'Text="自身输入"') { throw 'Shortcut settings must name the quick-input action as 打开输入框.' }
# D3：这条快捷键现在同时开关他人语音识别与字幕浮窗，必须统一改名为「他人语音」，
# 提示语为「开始 / 停止他人语音识别」；设置页三行为「打开输入框 / 他人语音 / 自身语音」。
if ($settingsMarkup -notmatch 'Text="他人语音"' -or
    $settingsMarkup -notmatch 'Text="开始 / 停止他人语音识别"' -or
    $settingsMarkup -match 'Text="字幕"' -or
    (Get-Content -Raw $guidePage) -notmatch 'Text="他人语音"' -or
    (Get-Content -Raw $guidePage) -notmatch 'Text="开始 / 停止他人语音识别"') {
    throw 'The caption shortcut must be named 他人语音 with the 开始 / 停止他人语音识别 hint in the settings and guide pages.'
}
# D9：快捷键不再手打字符串，也不再是 TextBox（WinUI 自带清除按钮「X」看起来像文本，
# 还会把已输入的手势清空）。三个字段改为点击后直接按键录制的捕获框：合法组合立即校验、立即落盘，
# Esc 取消、非法/冲突组合显示红框与说明，绝不表现为默默恢复旧值。
if ($settingsMarkup -notmatch 'Click="OnHotkeyCaptureClick"' -or
    $settingsMarkup -notmatch 'PreviewKeyDown="OnHotkeyCapturePreviewKeyDown"' -or
    $settingsMarkup -notmatch 'LostFocus="OnHotkeyCaptureLostFocus"' -or
    $settingsMarkup -notmatch 'AutomationProperties\.Name="打开输入框快捷键"' -or
    $settingsMarkup -notmatch 'AutomationProperties\.Name="他人语音快捷键"' -or
    $settingsMarkup -notmatch 'AutomationProperties\.Name="自身语音快捷键"' -or
    $settingsMarkup -notmatch 'x:Name="HotkeyStatus"' -or
    $settingsSource -notmatch 'HotkeyBinding\.Normalize' -or
    $settingsSource -notmatch 'InputKeyboardSource\.GetKeyStateForCurrentThread' -or
    $settingsSource -notmatch 'VirtualKey\.Escape' -or
    $settingsSource -notmatch 'VirtualKey\.Back' -or
    $settingsSource -notmatch 'ShowHotkeyStatus' -or
    $settingsSource -notmatch '"未设置"') {
    throw 'Shortcut editors must capture a real chord, save it immediately, and explain rejected chords instead of silently restoring the old value.'
}
if ($settingsMarkup -match '<TextBox x:Name="(QuickInput|Voice|SelfVoice)HotkeyBox"' -or $settingsSource -match 'ConfirmHotkeyChangeAsync|RestoreHotkey|IsHotkeyBox') {
    throw 'Shortcut editors must not go back to a text box with a built-in delete button and a restore-the-old-value path.'
}
if ($mainWindowSource -notmatch 'SettingsPage\.IsHotkeyCaptureActive' -or $mainWindowSource -notmatch 'string\.IsNullOrWhiteSpace\(gesture\)') {
    throw 'The global poller must stand down while a shortcut is recorded and must honour a cleared (empty) shortcut instead of resurrecting the default.'
}

# The run page is an operational overview. Manual text translation belongs to
# the dedicated quick-input flow and must not be duplicated here.
if ($runMarkup -match '快速文字翻译|InputTextBox|翻译文字' -or $runSource -match 'OnTranslateClicked') { throw 'RunPage must not duplicate the quick text translation workflow.' }
if ($runMarkup -notmatch '自身语音' -or $runSource -notmatch 'OnOpenSelfVoiceClicked' -or $runSource -notmatch 'SelfMessagePage') { throw 'RunPage must expose a direct self-voice entry.' }
if ($runMarkup -match '使用前检查|Ctrl\+Alt\+I|按 F7|按F7' -or $runMarkup -match '管理翻译服务') { throw 'RunPage must use concise, current labels without stale shortcut or defensive setup text.' }
if ($inputMarkup -notmatch '语音输入|自身语音|最近输出|VRChat' -or $inputMarkup -notmatch '麦克风' -or $inputMarkup -match '激活范围|SelfVoiceScopeBox|x:Name="MessageBox"') { throw 'Self input page must expose compact voice controls and output preview without an extra desktop input form.' }
if ($inputMarkup -match 'OSC Chatbox' -or (Get-Content -Raw ($inputPage.Replace('.xaml', '.xaml.cs'))) -match 'VRChat Chatbox') { throw 'Self input UI must use the user-facing VRChat destination label.' }
$inputCodeForLayout = Get-Content -Raw $inputPage.Replace('.xaml', '.xaml.cs')
if ($inputMarkup -match '激活范围|SelfVoiceScopeBox' -or $inputCodeForLayout -match 'ActivationScope|SelfVoiceScopeBox') { throw 'Self voice must not expose or persist an activation-scope setting; VRChat is the fixed target.' }
if ($inputMarkup -notmatch 'ProgressRing|EntranceThemeTransition' -or $inputMarkup -notmatch 'SelfVoiceStartButton' -or $inputMarkup -match 'SelfVoiceEnabledSwitch|OnContent="开"|OffContent="关"' -or $inputCodeForLayout -notmatch 'SelfVoicePulse\.IsActive|OnSelfVoiceStartClicked' -or $inputCodeForLayout -notmatch 'AnimateMicrophoneLevel|_microphoneLevelTimer|LevelChanged') { throw 'Self voice must provide one direct start/stop action, a visible animated state, and measured volume activity feedback.' }
if ($inputMarkup -notmatch 'MicrophoneLevelHost' -or $inputMarkup -notmatch 'x:Name="MicrophoneLevel"' -or $inputCodeForLayout -notmatch 'PlaceStatusElement\(SelfVoiceStatusLine') { throw 'Self voice status must keep the microphone level meter and lay the status line out for the compact column.' }
if ($quickInputSource -notmatch 'TextBox' -or $quickInputSource -match 'ComboBox|目标语言|第二语言|quick-target-language|quick-secondary-language' -or $quickInputSource -notmatch 'Translate(Self)?Async' -or $quickInputSource -notmatch 'SendChatboxAsync' -or $quickInputSource -notmatch 'SetTranslationPreview') { throw 'The global quick-input overlay must accept Chinese text, read its saved language pair from the input page, translate it, send it through OSC, and update the preview without duplicate language controls.' }
if ($quickInputSource -match 'AcceptsReturn\s*=\s*true' -or $quickInputSource -notmatch 'e\.Key\s*==\s*Windows\.System\.VirtualKey\.Enter' -or $quickInputSource -match '翻译并发送|_send\b' -or $quickInputSource -notmatch 'TranslationOutputFormatter\.FormatForChatbox' -or $quickInputSource -notmatch 'new\s+OverlayWindowController') { throw 'The quick-input overlay must submit on Enter without a send button, build its OSC payload through the shared chatbox formatter (message length limit), and use the shared native-window controller.' }
$legacyOverlayChrome = Join-Path $desktopSource 'Pages\OverlayWindowChrome.cs'
if (Test-Path -LiteralPath $legacyOverlayChrome) {
    throw 'The legacy custom OverlayWindowChrome implementation must remain deleted.'
}
if ($overlayControllerSource -notmatch 'OverlappedPresenter' -or
    $overlayControllerSource -notmatch 'SetBorderAndTitleBar\(hasBorder:\s*true,\s*hasTitleBar:\s*true\)' -or
    $overlayControllerSource -notmatch 'IsResizable\s*=\s*true' -or
    $overlayControllerSource -notmatch 'SetLayeredWindowAttributes' -or
    $overlayControllerSource -notmatch 'WsExLayered' -or
    $overlayControllerSource -match 'WM_NCHITTEST|WmNcHitTest|InputNonClientPointerSource|SetWindowRgn|CreateRoundRectRgn|CallWindowProc|SetWindowSubclass|GetCursorPos|mouse_event') {
    throw 'Game overlays must use standard captioned Windows windows with native movement/resizing and layered opacity, without custom hit testing or window regions.'
}
if ($mainWindowSource -notmatch 'StartGlobalHotkeys|PollGlobalHotkeys|GetAsyncKeyState' -or $mainWindowSource -notmatch 'OverlayWindowHost|ToggleQuickInput') { throw 'The shell must expose a working global shortcut route to the quick-input overlay.' }
if ($mainWindowSource -notmatch 'Closed\s*\+=\s*OnWindowClosed' -or $mainWindowSource -notmatch 'OnWindowClosed' -or $mainWindowSource -notmatch 'OverlayWindowHost\.CloseAll') { throw 'Closing the main shell must stop global polling and close both overlays.' }
if ($overlayHostSource -notmatch 'public\s+static\s+void\s+CloseAll' -or $overlayHostSource -notmatch 'ClosePermanently') { throw 'OverlayWindowHost must permanently destroy native overlay windows during shell shutdown.' }
if ((Get-Content -Raw (Join-Path $desktopSource 'Pages\SelfMessagePage.xaml.cs')) -notmatch 'LocalSpeech|RecognizeSelfVoiceSamplesAsync|ToggleSelfVoiceFromHotkey|SendChatboxAsync') { throw 'Self-voice must use the local speech service and retain the OSC send path.' }
if ((Get-Content -Raw (Join-Path $desktopSource 'Pages\SelfMessagePage.xaml.cs')) -match 'SpeechRecognizer|Windows\.Media\.SpeechRecognition') { throw 'Self-voice must not fall back to Windows SpeechRecognizer.' }
if ($inputMarkup -match '发送格式|仅发送译文|原文 \+ 译文|仅发送原文') { throw 'Quick input must not expose an unused send-format selector.' }
$inputSource = Get-Content -Raw $inputPage.Replace('.xaml', '.xaml.cs')
if ($inputSource -notmatch 'AppDataFiles\.SelfVoiceSettings' -or $inputSource -notmatch 'ToggleSelfVoiceFromHotkey' -or $inputSource -notmatch 'State\.AudioCapture\.Create\(AudioCaptureRequest\.Microphone' -or $inputSource -notmatch 'SamplesReady' -or $inputSource -notmatch 'Task\.WhenAny' -or $inputSource -match 'MediaCapture') { throw 'Self voice must retain persisted controls, a hotkey entry point, and a real microphone frame test.' }
if ($inputSource -match 'WindowsAudioCapture|NAudio\.|VrcTranslate\.Infrastructure\.Speech') { throw 'Self voice page must use the application audio factory instead of a platform implementation.' }
# Recognition sessions moved to the application-scoped host so page navigation
# no longer stops them; the wiring invariant now lives there.
if ($sessionHostSource -notmatch 'LocalSpeechCaptureSession' -or
    $sessionHostSource -notmatch 'AudioCaptureMode\.Microphone' -or
    $sessionHostSource -notmatch 'AudioCaptureRequest\.Microphone' -or
    $sessionHostSource -notmatch 'session\.StartAsync' -or
    $sessionHostSource -notmatch 'session\.DisposeAsync' -or
    $appStateSource -notmatch 'ShutdownSpeechAsync') {
    throw 'Self voice must connect the microphone capture session with a microphone source request (decision 1) and release it on stop/shutdown via the session host.'
}
if ($inputSource -notmatch 'TranslationPreviewChanged' -or $inputSource -notmatch 'RecentOriginal' -or $inputSource -notmatch 'RecentTranslation') { throw 'The main quick-input page must update its original/translated preview after a send.' }
if ($inputMarkup -match 'SelfVoiceLanguageBox|Header="识别语言"' -or $inputSource -match 'SelfVoiceLanguageBox') { throw 'Own voice input must remain Simplified Chinese and must not expose a recognition-language selector.' }
if ($inputMarkup -notmatch 'Text="第一语言"' -or $inputMarkup -notmatch 'Text="第二语言"' -or $inputMarkup -notmatch 'PrimaryTargetBox' -or $inputMarkup -notmatch 'SecondaryTargetBox' -or $inputMarkup -match 'TargetLanguageExpander' -or $inputSource -notmatch 'TranslateSelfAsync|LastSecondaryTranslatedText') { throw 'The input page must expose visible primary and optional second output-language settings with a dual preview.' }
if ($appStateSource -notmatch 'TranslationTargetSet|SelfTranslationTargets|TranslateSelfAsync|LastSecondaryTranslatedText') { throw 'AppState must persist and route the own-message target language pair.' }
if ($voiceMarkup -match '目标进程|刷新进程|显示字幕覆盖层|VRChat\.exe' -or $voiceMarkup -match '<ComboBox[^>]*(识别语言|目标语言)|Header="(识别语言|目标语言)"' -or $voiceMarkup -match 'ComboBoxItem Content="当前系统输出"') { throw 'VoicePage must keep process and subtitle-language controls out of the main page.' }
if ($voiceMarkup -match '当前翻译方案|默认翻译方案|识别后的文字会使用默认翻译服务') { throw 'VoicePage must not present translation-route details in the speech-recognition workflow.' }
if ($voiceMarkup -notmatch '本地语音模型' -or $voiceMarkup -notmatch 'SenseVoice Small' -or $voiceMarkup -notmatch '管理模型' -or $voiceMarkup -notmatch 'Text="字幕窗口"') { throw 'VoicePage must expose local model management and one concise subtitle-window section.' }
# D3：字幕浮窗不再有独立入口，它只随他人语音识别出现与消失；页面上原来那个
# 「打开字幕」按钮必须保持删除状态。
if ($voiceMarkup -match 'AutomationProperties.Name="打开字幕"|Content="打开字幕"') { throw 'VoicePage must not keep a window-only subtitle entry; the caption surface follows the other-player recognition switch.' }
if ($voiceSource -notmatch 'State\.LocalSpeech|GetModelStatus|InstallModelAsync|RecognizeLocalSamplesAsync') { throw 'VoicePage must expose the local SenseVoice model state and use the application speech boundary.' }
if ($voiceSource -match 'SpeechRecognizer|Windows\.Media\.SpeechRecognition') { throw 'VoicePage must not use Windows SpeechRecognizer.' }
# Phase 2 接线：他人语音不再固定请求系统回环，而是交给自适应回环协调器，由 VRChat
# 进程监视器通过 ApplyTargetAsync 推送目标；停止时先释放监视器再释放采集会话。
if ($sessionHostSource -notmatch 'LocalSpeechCaptureSession' -or
    $sessionHostSource -notmatch 'new AdaptiveLoopbackAudioCapture' -or
    $sessionHostSource -notmatch 'ApplyTargetAsync' -or
    $sessionHostSource -notmatch 'VrchatProcessWatcher' -or
    $sessionHostSource -notmatch 'monitor\.DisposeAsync' -or
    $sessionHostSource -notmatch 'AudioCaptureMode\.SystemLoopback' -or
    $sessionHostSource -notmatch 'session\.StartAsync' -or
    $sessionHostSource -notmatch 'session\.DisposeAsync') {
    throw 'The session host must connect the other-player session to the adaptive loopback coordinator, keep it steered by the VRChat process monitor, and release both on stop/shutdown.'
}
if ($voiceSource -match 'WindowsAudioCapture|NAudio\.|VrcTranslate\.Infrastructure\.Speech') { throw 'Voice page must use the application audio factory instead of a platform implementation.' }
# Phase 4 接线：状态卡内一个紧凑来源徽标（VRChat 音频 / 系统声音 / 系统声音（兼容模式）），
# 数据来自 LocalSpeechCaptureSession 的 SourceState / SourceChanged；首次降级只提示一次，
# 恢复后提示自动消失；不新增进程选择、音频源下拉或白名单。
if ($sessionHostSource -notmatch 'SourceChanged' -or
    $sessionHostSource -notmatch 'SourceState' -or
    $sessionHostSource -notmatch 'ConsumeCaptureFallbackNotice') {
    throw 'The session host must expose the capture source state and the one-time fallback notice to the page.'
}
if ($voiceMarkup -notmatch 'VoiceCaptureSourceBadge' -or
    $voiceMarkup -notmatch 'x:Name="VoiceCaptureSourceText"' -or
    $voiceMarkup -notmatch 'voice-capture-source') {
    throw 'VoicePage must show one compact capture-source badge inside the status card.'
}
if ($voiceSource -notmatch 'AudioCaptureSourceKind\.ProcessLoopback' -or
    $voiceSource -notmatch 'VRChat 音频' -or
    $voiceSource -notmatch 'AudioCaptureSourceKind\.SystemLoopbackFallback' -or
    $voiceSource -notmatch '系统声音（兼容模式）' -or
    $voiceSource -notmatch '系统声音"') {
    throw 'The capture-source badge must name VRChat audio, system audio and the compatibility fallback.'
}
if ($voiceSource -notmatch 'SourceChanged' -or
    $voiceSource -notmatch 'DispatcherQueue\.TryEnqueue' -or
    $voiceSource -notmatch 'ConsumeCaptureFallbackNotice' -or
    $voiceSource -notmatch '无法只采集 VRChat 声音，已改用系统声音' -or
    $voiceSource -notmatch 'VoiceInfo\.IsOpen = false') {
    throw 'The badge must follow the session source events on the UI thread, hint once on the first fallback, and withdraw the hint after recovery.'
}
if ([regex]::Matches($voiceMarkup, '<ComboBox[\s>]').Count -ne 1) {
    throw 'VoicePage must keep exactly one selector (the local model box); no capture-source or process picker may be added.'
}
if ($voiceMarkup -match 'Windows 系统识别 · 无需密钥') { throw 'VoicePage must keep the recognition status label concise.' }
if ([regex]::Matches($voiceMarkup, 'AutomationProperties.Name="打开字幕"').Count -ne 0) { throw 'The window-only subtitle entry must stay deleted so the caption surface cannot be toggled apart from recognition.' }
if ($voiceSource -notmatch 'State\.OtherPlayerCaption\.SetEnabledAsync' -or
    ([regex]::Matches($voiceMarkup, 'Click="OnStartClicked"').Count -ne 1)) {
    throw 'VoicePage must drive other-player recognition through the one serialized master switch that also owns the caption surface.'
}
if ($voiceMarkup -notmatch 'ProgressRing|EntranceThemeTransition' -or $voiceMarkup -notmatch 'VoiceAudioLevel' -or $voiceSource -notmatch 'VoicePulse\.IsActive' -or $voiceSource -notmatch 'LevelChanged|OnAudioLevelChanged') { throw 'VoicePage must provide a compact animated recognition state and measured audio activity.' }
if ($voiceSource -notmatch 'ContentDialog' -or $voiceSource -notmatch 'OtherPlayerCaption' -or $voiceSource -notmatch 'InstallModelAsync' -or $voiceSource -notmatch 'RemoveModelAsync') { throw 'VoicePage must provide a local model management dialog and start captions through the shared master switch.' }
if ($voiceMarkup -match '1\s+音频来源|2\s+识别服务|3\s+字幕窗口') { throw 'VoicePage must not use numbered explanatory cards.' }
if ($overlayMarkup -match '识别语言|目标语言|显示(?:内容)?|他人语音字幕|等待') { throw 'Subtitle overlay must stay minimal: automatic recognition and fixed Simplified Chinese output have no visible labels or selectors.' }
if ($overlayMarkup -match 'SourceLanguageBox|TargetLanguageBox|DisplayModeBox|OverlayStatusText|OverlayStatusDot|Content="他人语音字幕"') { throw 'Subtitle overlay must not render redundant names, language selectors, display selectors, or status widgets.' }
if ($overlayMarkup -notmatch 'AutomationProperties.AutomationId="subtitle-text"' -or
    $overlayCodeSource -notmatch 'SelectedTargetLanguage\s*=>\s*"zh-CN"' -or
    $overlayCodeSource -notmatch 'SelectedSourceLanguage\s*=>\s*"auto"' -or
    $overlayCodeSource -notmatch 'new\s+OverlayWindowController') {
     throw 'Subtitle overlay must expose one compact surface, keep automatic recognition and Simplified Chinese output, and use the shared native-window controller.'
 }
 # D4：左侧那个看起来像麦克风的圆形活动标识已删除（它让人误会软件在用麦克风收音），
 # 活动反馈由语音页的电平表承担；右侧那些早先被删掉的装饰同样不得回来。
 if ($overlayMarkup -match '(?i)waveform|wave-bar|wavebar|right-decoration|audio-bars|subtitle-activity-mark|PulseRing|Ellipse|FontIcon|Glyph=') {
     throw 'The caption surface must stay text-only: no activity mark, icon or decorative indicator may return.'
 }
# D1：长对话改成消息条滚动列表。消息只在本次运行期间存在（不写盘、没有清空入口），
# 停止识别只暂停追加而不改变已有消息，用户上翻时不得被强制拉回底部。
if ($captionBufferSource -notmatch 'DefaultCapacity\s*=\s*200' -or
    $captionBufferSource -notmatch 'RemoveAt\(0\)' -or
    $captionBufferSource -notmatch 'IsPaused') {
    throw 'Caption messages must be a bounded log (200 newest, oldest dropped) that can be paused without clearing it.'
}
if ($overlayMarkup -notmatch '<ScrollViewer[^>]*subtitle-text' -or
    ([regex]::Matches($overlayMarkup, 'AutomationProperties.AutomationId="subtitle-text"').Count -ne 1) -or
    $overlayMarkup -match 'x:Name="SubtitleText"|清空') {
    throw 'The subtitle overlay must render one scrolling message list: no single-caption block and no clear action.'
}
if ($overlayMarkup -notmatch 'subtitle-new-message' -or $overlayMarkup -notmatch '有新消息' -or
    $overlayCodeSource -notmatch 'SubtitleCaptionBuffer' -or
    $overlayCodeSource -notmatch 'AppendCaption' -or
    $overlayCodeSource -notmatch 'ScrollToLatest' -or
    $overlayCodeSource -notmatch 'OnCaptionViewChanged' -or
    $overlayCodeSource -notmatch 'NewCaptionHint') {
    throw 'The subtitle overlay must append messages, follow the newest one and offer a new-message hint instead of forcing the view down.'
}
if ($overlayCodeSource -notmatch 'SetStreamPaused' -or
    $overlayHostSource -notmatch 'SubtitleVoice\.RunningChanged' -or
    $overlayHostSource -notmatch 'SetStreamPaused') {
    throw 'Stopping recognition must pause the caption stream without clearing the messages already shown, and restarting must continue below them.'
}
if ($overlayCodeSource -notmatch 'SubtitleCaptionSettings\.Read\(\)' -or
    $overlayCodeSource -notmatch 'ApplyContentMode' -or
    $captionSettingsSource -notmatch 'ContentPropertyName\s*=\s*"SubtitleContent"' -or
    $captionSettingsSource -notmatch 'translated-only') {
    throw 'The caption surface must read the persisted 仅译文 / 译文 + 原文 option and follow later changes.'
}
if ($voiceMarkup -notmatch 'subtitle-content-toggle' -or
    $voiceSource -notmatch 'SubtitleCaptionSettings' -or
    $voiceSource -notmatch 'OverlayWindowHost\.ApplySubtitleContentMode' -or
    $voiceSource -notmatch 'SubtitleContent') {
    throw 'VoicePage must offer the caption content option next to the subtitle opacity and persist it in the voice settings document.'
}
# D4：字幕消息水平居中——译文、原文和说话人标签行都按整条消息的宽度居中。
if (([regex]::Matches($overlayCodeSource, 'TextAlignment\s*=\s*TextAlignment\.Center').Count -lt 2) -or
    $overlayCodeSource -notmatch 'HorizontalAlignment\s*=\s*HorizontalAlignment\.Stretch') {
    throw 'Caption messages must be centred: every rendered line, including the speaker label, uses TextAlignment.Center across the message width.'
}
# D6：字幕样式向播放器字幕靠——译文 18px（窄窗低于 420px 时降一档到 16px）、原文 13px 且更暗，
# 两者共用同一对齐、同一左右内边距与同一行距（约 1.35），固定「译文在上、原文在下」；
# 去掉圆角气泡，改成整块深色表面 + 消息之间的细分隔，消息间距 8~10px；
# 窗口高度随内容收缩（「有新消息」提示保持不变），浮窗不得因此多出任何控件。
if ($overlayCodeSource -notmatch 'TranslatedFontSize = 18d' -or
    $overlayCodeSource -notmatch 'NarrowTranslatedFontSize = 16d' -or
    $overlayCodeSource -notmatch 'NarrowSurfaceWidth = 420d' -or
    $overlayCodeSource -notmatch 'OriginalFontSize = 13d' -or
    $overlayCodeSource -notmatch 'LineHeightRatio = 1\.35d' -or
    $overlayCodeSource -notmatch 'MessageSpacing = (?:8|9|10)d' -or
    $overlayCodeSource -match 'CornerRadius' -or
    $overlayCodeSource -notmatch 'SeparatorBrush' -or
    $overlayCodeSource -notmatch 'LineStackingStrategy\.BlockLineHeight' -or
    $overlayCodeSource -notmatch 'FitSurfaceHeight' -or
    $overlayCodeSource -notmatch '\.ResizeKeepingTop\(' -or
    $overlayControllerSource -notmatch 'public bool ResizeKeepingTop' -or
    ([regex]::Matches($overlayMarkup, '<Button').Count -ne 1)) {
    throw 'Caption styling must follow the confirmed D6 design: 18px translation with a 16px narrow step, 13px dimmer recognized line, one shared alignment/padding/line height, no rounded bubble, a hairline separator, content-following window height, and no extra control on the surface.'
}
# D7：消息列表顶部对齐。用户把浮窗拉大后，内容必须出现在上半部分，而不是像旧布局那样
# 贴在底边、上方留一大片空白；高度跟随内容时窗口顶边固定（向下生长），与消息的生长方向一致，
# 已经显示的句子不会被新消息推着往上跳。窗口顶边固定必须仍然保留 D6 的「用户改过高度就不再自动改」。
if ($overlayMarkup -notmatch '(?s)<StackPanel x:Name="CaptionMessages"\s+HorizontalAlignment="Stretch"\s+VerticalAlignment="Top"') {
    throw 'The caption message list must be anchored to the top, so an enlarged surface shows its messages in the upper part instead of against the bottom edge.'
}
if ($overlayMarkup -match 'VerticalContentAlignment="Bottom"') {
    throw 'The bottom-anchored caption scroll viewer must stay removed: it is what pushed the messages against the bottom edge of an enlarged surface.'
}
if ($overlayCodeSource -match 'ResizeKeepingBottom' -or $overlayControllerSource -match 'ResizeKeepingBottom') {
    throw 'The caption strip must grow downwards from its top edge; growing upwards belonged to the removed bottom-anchored list.'
}
# D10：同一个方法现在也带一个显式宽度（全新安装按缩放换算宽高），所以这里断言的是
# 「顶边取自当前位置、宽度不来自读者实时矩形（尺寸只由本方法决定）」，而不是某个字面调用。
# 精确取「带宽度的那个重载」的方法体：表达式体的单参重载会先命中，所以不能只用 .*? 放宽。
$followResizeStart = $overlayControllerSource.IndexOf(
    'public bool ResizeKeepingTop(int width, int height)',
    [StringComparison]::Ordinal)
if ($followResizeStart -lt 0) {
    throw 'The native-window controller must expose a width-aware ResizeKeepingTop overload for the DIP default size.'
}
# 注意：方法上方的 XML 注释里就出现过 MoveAndResize 这个词，所以要匹配真正的那次调用
# （它后面紧跟着换行和 RectInt32 实参），不能只匹配到单词本身。
$followResize = [regex]::Match(
    $overlayControllerSource.Substring($followResizeStart),
    '(?s)^.*?MoveAndResize\(\s*\r?\n')
if (-not $followResize.Success -or
    $followResize.Value -notmatch 'RectInt32\(\s*\n?\s*position\.X,\s*\n?\s*position\.Y,' -or
    $followResize.Value -notmatch 'targetWidth\s*>\s*0\s*\?\s*targetWidth') {
    throw 'Following the content must keep the top edge and must size the surface from this method rather than from the window''s live rectangle.'
}
if ($overlayCodeSource -notmatch '_readerOwnsHeight') {
    throw 'Following the content must keep the reader''s own height final for the session.'
}
# D6：渐进式字幕——识别完成即先显示原文（含说话人标签），译文到达后填进同一条消息；
# 不新增条数、不重排、不改 OSC 行（OSC 仍是译文）。译文为空或失败时该条只剩原文，
# 并且必须解析成「没有译文」而不是永远等待的占位。
if ($captionBufferSource -notmatch 'SubtitleTranslationState' -or
    $captionBufferSource -notmatch 'SubtitleTranslationState\.Pending' -or
    $captionBufferSource -notmatch 'SubtitleTranslationState\.Unavailable' -or
    $captionBufferSource -notmatch 'TrySetTranslation' -or
    $overlayCodeSource -notmatch 'AppendRecognizedCaption' -or
    $overlayCodeSource -notmatch 'FillCaptionTranslation' -or
    $overlayCodeSource -notmatch 'MissingTranslationSuffix' -or
    $overlayHostSource -notmatch 'AppendRecognizedSubtitleFromAnyThread' -or
    $overlayHostSource -notmatch 'FillSubtitleTranslationFromAnyThread' -or
    $overlayHostSource -match 'AppendSubtitleFromAnyThread' -or
    $sessionHostSource -notmatch '(?s)protected override long PublishRecognized.*?AppendRecognizedSubtitleFromAnyThread' -or
    $sessionHostSource -notmatch '(?s)TranslateAsync.*?FillSubtitleTranslationFromAnyThread' -or
    $sessionHostSource -notmatch 'FillSubtitleTranslationFromAnyThread\(recognizedId, null\)') {
    throw 'Progressive captions must show the recognized line first and fill the translation into that same message: recognition owns the message, translation only completes it, and a failed or empty translation resolves the message instead of leaving it pending.'
}
if ($quickInputSource -notmatch 'new\s+OverlayWindowController' -or
    $quickInputSource -notmatch 'OverlayWindowHost\.GetSavedLayout' -or
    $quickInputSource -notmatch 'OverlayWindowHost\.SaveLayout' -or
    $overlayCodeSource -notmatch 'new\s+OverlayWindowController' -or
    $overlayCodeSource -notmatch 'OverlayWindowHost\.GetSavedLayout' -or
    $overlayCodeSource -notmatch 'OverlayWindowHost\.SaveLayout') {
    throw 'Both overlays must pass a concrete default size and their saved layout through the shared native-window controller.'
}
if ($overlayControllerSource -notmatch 'BestEffort\(\(\)\s*=>\s*ConfigurePresenter' -or
    $overlayControllerSource -notmatch 'BestEffort\(\(\)\s*=>\s*RestoreLayout' -or
    $overlayControllerSource -notmatch 'hwnd\s*==\s*IntPtr\.Zero\s*\|\|\s*!ShowWindow' -or
    $quickInputSource -notmatch 'OverlayWindowController\.ShowFallback\(this, activate\)' -or
    $overlayCodeSource -notmatch 'OverlayWindowController\.ShowFallback\(this, activate\)') {
    throw 'Optional native presentation failures must retain an openable compatibility path for both overlays.'
}
if ($overlayHostSource -notmatch 'AppDataFiles\.OverlayLayout' -or
    $overlayHostSource -notmatch 'GetSavedLayout' -or
    $overlayHostSource -notmatch 'SaveLayout' -or
    $overlayHostSource -notmatch '\.Flush\(\)' -or
    $overlayControllerSource -notmatch 'RestoreLayout' -or
    $overlayControllerSource -notmatch 'PublishLayout' -or
    $overlayControllerSource -notmatch 'TryGetLayout' -or
    $overlayControllerSource -notmatch 'AppWindow\.Changed' -or
    $overlayLayoutContract -notmatch 'record struct OverlayWindowLayout' -or
    $overlayLayoutStore -notmatch 'ScheduleSaveLocked' -or
    $overlayLayoutStore -notmatch 'File\.Move\(temporaryPath') {
     throw 'Overlay position and size persistence must use a layered layout contract, coalesced atomic storage, and AppWindow change callbacks.'
 }
 # Shutdown can defer Window.Closed while the dispatcher is unwinding. Keep a
 # direct pre-close snapshot so the final user move/resize is not lost before
 # the layout store is flushed.
 $closeAllMatch = [regex]::Match(
     $overlayHostSource,
     '(?s)public static void CloseAll\(\).*?(?=\n\s*///|\n\s*public static|\z)')
 if (-not $closeAllMatch.Success -or
     $closeAllMatch.Value.IndexOf('SaveCurrentLayout(quickInput', [StringComparison]::Ordinal) -lt 0 -or
     $closeAllMatch.Value.IndexOf('SaveCurrentLayout(subtitle', [StringComparison]::Ordinal) -lt 0 -or
     $closeAllMatch.Value.IndexOf('SaveCurrentLayout(quickInput', [StringComparison]::Ordinal) -gt
         $closeAllMatch.Value.IndexOf('CloseWindow(quickInput', [StringComparison]::Ordinal) -or
     $closeAllMatch.Value.IndexOf('SaveCurrentLayout(subtitle', [StringComparison]::Ordinal) -gt
         $closeAllMatch.Value.IndexOf('CloseWindow(subtitle', [StringComparison]::Ordinal)) {
     throw 'Overlay shutdown must snapshot both native rectangles before closing windows.'
 }
if ($quickInputSource -match 'quick-secondary-language|quick-target-language|第二语言|目标语言|展开第二语言|收起第二语言|ComboBox' -or
    $quickInputSource -notmatch 'Content\s*=\s*_surface' -or
    $quickInputSource -match 'windowSurface' -or
    $quickInputSource -match 'private readonly Border _surface' -or
    $quickInputSource -match 'CornerRadius\s*=\s*new CornerRadius' -or
    $quickInputSource -notmatch 'private readonly Grid _surface' -or
    $quickInputSource -notmatch 'RequestedTheme\s*=\s*ElementTheme\.Dark' -or
    $quickInputSource -notmatch 'Background\s*=\s*Brush\("#[0-9A-Fa-f]{6}"\)' -or
    $quickInputSource -notmatch 'Opacity\s*=\s*1\b' -or
    $quickInputSource -notmatch 'BorderThickness\s*=\s*new Thickness\(0\)') {
     throw 'Quick-input overlay must fill the opaque native client area with one dark Grid and leave adjustable transparency to the HWND.'
 }
 # The stock WinUI TextBox template paints a new BorderElement background in
 # its focused and pointer-over states.  Keep those theme resources transparent
 # so focusing the editor cannot reintroduce the inner rectangle reported in
 # manual screenshots.
 $inputVisualResourceKeys = @(
     'TextControlBackground',
     'TextControlBackgroundPointerOver',
     'TextControlBackgroundFocused',
     'TextControlBackgroundDisabled',
     'TextControlBorderBrush',
     'TextControlBorderBrushPointerOver',
     'TextControlBorderBrushFocused',
     'TextControlBorderBrushDisabled')
 foreach ($resourceKey in $inputVisualResourceKeys) {
     if ($quickInputSource -notmatch ([regex]::Escape('"' + $resourceKey + '"'))) {
         throw "Quick-input TextBox does not neutralize the $resourceKey visual-state resource."
     }
 }
 # 编辑行现在是贴顶的固定 46px 条带，正文靠上下对称留白垂直居中；左右内边距必须
# 保持 0，编辑文字才与下方结果行左对齐，焦点也仍然只由编辑行外部那条下划线表达，
# 不会在编辑框里画出第二个矩形。
if ($quickInputSource -notmatch 'UseSystemFocusVisuals\s*=\s*false' -or
     $quickInputSource -notmatch 'TextControlBorderThemeThicknessFocused' -or
     $quickInputSource -notmatch 'Padding\s*=\s*new Thickness\(0,\s*\d+(?:\.\d+)?,\s*0,\s*\d+(?:\.\d+)?\)') {
     throw 'Quick-input focus visuals must stay transparent and borderless in every TextBox state, with a zero horizontal inset that keeps the editor text flush with the result lines.'
 }
if ($outputFormatterSource -notmatch 'Separator\s*=\s*" / "' -or
    $outputFormatterSource -notmatch 'AddIfPresent\(parts, primaryTranslation\)' -or
    $outputFormatterSource -notmatch 'AddIfPresent\(parts, secondaryTranslation\)' -or
    $outputFormatterSource -notmatch 'AddIfPresent\(parts, originalText\)' -or
    $outputFormatterSource -notmatch 'OscChatboxMaxUtf16Length\s*=\s*144') {
    throw 'Translation output formatting must use one slash-separated primary/secondary/original order and the VRChat 144 UTF-16 limit.'
}
if ($quickInputSource -notmatch 'TranslationOutputFormatter\.FormatForChatbox' -or
    $quickInputSource -notmatch 'result\.Primary\.TranslatedText' -or
    $sessionHostSource -notmatch 'TranslationOutputFormatter\.FormatForChatbox' -or
    $sessionHostSource -notmatch 'TranslationOutputFormatter\.TrimForOsc') {
    throw 'Own-input preview and own-voice OSC output must share the Core translation formatter.'
}
if ($sessionHostSource -notmatch 'TranslationOutputFormatter\.TrimForOsc' -or
    $voiceSource -match 'TrimForChatbox\s*\(' -or
    $voiceSource -match 'CombinedText|FormattedText') {
    throw 'Subtitle OSC output must use the shared length guard while keeping its single Simplified Chinese translation.'
}
# 句内并行：一句多目标必须同时发请求（串行时用户要等两段译文之和），并且主/副顺序不能变。
# 主目标失败仍然整句失败、副目标失败降级为单目标，两条语义都要在代码里看得见。
$translationServiceSource = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Application\Translation\TranslationService.cs')
$translateManyBody = [regex]::Match(
    $translationServiceSource,
    '(?s)public async Task<TextTranslationBatchResult> TranslateManyAsync.*?\n    \}')
if (-not $translateManyBody.Success -or
    $translateManyBody.Value -notmatch 'Task\.WhenAll' -or
    $translateManyBody.Value -match 'foreach[\s\S]{0,400}?await TranslateAsync') {
    throw 'TranslateManyAsync must issue one request per target language concurrently; a serial foreach would make the user wait for the sum of both translations.'
}
if ($translateManyBody.Value -notmatch 'pending\[0\]\.IsCompletedSuccessfully' -or
    $translateManyBody.Value -notmatch 'new TranslationTargetSet\(targets\.PrimaryLanguage\)') {
    throw 'A failed secondary target must degrade to the primary-only result, while a failed primary must still fail the batch.'
}
if ($overlayMarkup -notmatch '<Grid x:Name="OverlaySurface"' -or
     $overlayMarkup -match 'x:Name="OverlaySurface"[\s\S]{0,400}(BorderBrush|BorderThickness|CornerRadius)=' -or
     $overlayMarkup -notmatch 'Background="#[0-9A-Fa-f]{6}"' -or
     $overlayMarkup -notmatch 'x:Name="CaptionScroll"' -or
     $overlayCodeSource -notmatch 'ExtendsContentIntoTitleBar\s*=\s*false' -or
     $overlayCodeSource -match 'PulseRing|OnVisualTimerTick|_visualPhase') {
     throw 'Subtitle overlay must fill the native client area with one dark Grid and the caption list, without the removed activity indicator or its animation.'
 }
if ($overlayControllerSource -notmatch '_window\.ExtendsContentIntoTitleBar\s*=\s*false' -or
    $overlayControllerSource -notmatch 'SetBorderAndTitleBar\(hasBorder:\s*true,\s*hasTitleBar:\s*true\)' -or
    $overlayControllerSource -notmatch 'IsAlwaysOnTop\s*=\s*true' -or
    $overlayControllerSource -notmatch 'ApplyOpacity' -or
    $overlayControllerSource -notmatch 'SetLayeredWindowAttributes' -or
    $overlayControllerSource -notmatch 'args\.Cancel\s*=\s*true' -or
    $overlayControllerSource -notmatch 'Hide\(\)') {
    throw 'Overlay controller must retain the standard title bar, native border, always-on-top behavior, layered opacity, and close-to-hide lifecycle.'
}
if ($overlayMarkup -match 'Shadow' -or $quickInputSource -match 'Shadow') { throw 'Game overlays must not add shadows over the game view.' }
if ($voiceSource -notmatch 'SpeechRecognitionRequest|State\.LocalSpeech' -or ($voiceSource -notmatch '"zh-CN"' -and $sessionHostSource -notmatch '"zh-CN"')) { throw 'VoicePage must use the local recognition boundary and keep translation output fixed to Simplified Chinese.' }
if ($voiceMarkup -notmatch 'voice-hotkey-summary' -or $voiceMarkup -notmatch '他人语音' -or $voiceMarkup -notmatch '开始 / 停止他人语音识别' -or $voiceSource -notmatch 'ReadGlobalVoiceHotkey') { throw 'VoicePage must display the configured 他人语音 shortcut with its 开始 / 停止他人语音识别 hint.' }
if ($mainWindowSource -notmatch 'OtherPlayerCaption\s*\.\s*ToggleAsync' -or $mainWindowSource -notmatch 'ModelNotReady') { throw 'The global 他人语音 shortcut must switch recognition and the caption surface together and report a missing model instead of staying silent.' }
if ($hotkeyContractsSource -notmatch 'NormalizeModifier' -or $hotkeyContractsSource -notmatch 'IsSupportedPrimary' -or $hotkeyContractsSource -notmatch 'functionKey\s+is\s+>=\s+1\s+and\s+<=\s+12') { throw 'Core hotkey normalization must define the same supported modifier and primary-key set as the desktop poller.' }
if ($settingsValidationSource -notmatch 'HotkeyBinding\.Normalize\(gesture\)' -or $settingsValidationSource -notmatch 'gestures\[normalized\]') { throw 'Workspace settings validation must use canonical hotkey normalization for conflicts and unsupported keys.' }
if ($mainWindowSource -notmatch 'HotkeyBinding\.Normalize' -or $mainWindowSource -notmatch 'return HotkeyBinding\.Normalize\(fallback\)') { throw 'Desktop global hotkeys must use the core canonical normalization and safely fall back for invalid persisted values.' }
# D9 后续：显示快捷键的地方必须与轮询同义——空字符串是用户主动清空（一律显示「未设置」，
# 绝不回落默认值），只有设置缺失 / 文件不可读 / 值非法才回落默认。Core 的 HotkeyDefaults
# 定义这条规则，语音页、自身语音页与指南页共用 Pages\HotkeyDisplay.cs 这一个读取器；
# 指南页的值必须来自设置，不能再把出厂默认值写死在 XAML 里。
$hotkeyDefaultsSource = Get-Content -Raw (Join-Path $coreSource 'Settings\HotkeyDefaults.cs')
$hotkeyDisplaySource = Get-Content -Raw (Join-Path $desktopSource 'Pages\HotkeyDisplay.cs')
$guideSource = Get-Content -Raw $guidePage.Replace('.xaml', '.xaml.cs')
if ($hotkeyDefaultsSource -notmatch 'ResolvePersisted' -or
    $hotkeyDefaultsSource -notmatch 'IsNullOrWhiteSpace\(stored\)' -or
    $hotkeyDefaultsSource -notmatch 'return HotkeyBinding\.Normalize\(fallback\)' -or
    $hotkeyDisplaySource -notmatch 'HotkeyDefaults\.ResolvePersisted' -or
    $hotkeyDisplaySource -notmatch '"未设置"' -or
    $hotkeyDisplaySource -notmatch 'ReadOtherPlayerVoice' -or
    $hotkeyDisplaySource -notmatch 'ReadSelfVoice' -or
    $voiceSource -notmatch 'HotkeyDisplay\.ReadOtherPlayerVoice' -or
    $inputSource -notmatch 'HotkeyDisplay\.ReadSelfVoice' -or
    $guideSource -notmatch 'HotkeyDisplay\.ReadOtherPlayerVoice' -or
    $voiceMarkup -match 'Text="F7"' -or
    $inputMarkup -match 'Text="Ctrl\+F8"' -or
    (Get-Content -Raw $guidePage) -match 'Text="(F7|Ctrl\+F8|Ctrl\+Alt\+I)"') {
    throw 'Every shortcut readout must share the poller semantics: a cleared shortcut stays 未设置 everywhere and only a missing, unreadable or unsupported value falls back to the default.'
}
# 翻译服务配置卡片（方案《翻译服务配置简化方案》A0）：只给控制台直达与额度要点，
# 额度必须带「以官网为准」，页面不得出现任何密钥或示例密钥。
$guideMarkup = Get-Content -Raw $guidePage
# 模型对比卡片（实验报告-识别与翻译.md 的实测结论）：5 个模型 + 推荐 + 计费口径 + 价格会变动。
# 模型名必须落在数据侧真值上：小米那份的 pro 版本已实测不推荐，引导页不得推荐它。
$xiaomiModels = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Infrastructure\Translation\XiaomiTranslationProvider.cs')
if ($guideMarkup -notmatch 'guide-comparison' -or
    $guideMarkup -notmatch '模型对比' -or
    $guideMarkup -notmatch '阿里云 · 专业版' -or
    $guideMarkup -notmatch '腾讯云 · 文本翻译' -or
    $guideMarkup -notmatch '阿里云 · 通用版' -or
    $guideMarkup -notmatch 'deepseek-flash' -or
    $guideMarkup -notmatch '小米 MiMo v2\.5' -or
    $guideMarkup -notmatch '700 万字符/月' -or
    $guideMarkup -notmatch '约 600 句' -or
    $guideMarkup -notmatch '价格会变动' -or
    $guideMarkup -match '0\.0000\d+ 元' -or
    $xiaomiModels -notmatch 'mimo-v2\.5-pro' -or
    $guideMarkup -match 'mimo-v2\.5-pro' -or
    $guideMarkup -match 'v4-pro') {
    throw 'Guide page comparison card must list the five recommended models with the 700 万字符/月 quota, the 推荐 line and the 价格会变动 caveat, and must never recommend the pro models that measured slower or worse.'
}
# 作者小店卡片：链接只能来自用户配置，XAML 里不得写死具体链接，卡片文案必须写明软件免费。
$shopSource = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Desktop\AuthorShop.cs')
if ($guideMarkup -notmatch 'guide-shop' -or
    $guideMarkup -notmatch '作者的小店' -or
    $guideMarkup -match 'https?://[^"'']*goofish' -or
    $guideMarkup -match 'https?://[^"'']*2\.taobao' -or
    $shopSource -notmatch 'ShopUrl' -or
    $shopSource -notmatch '软件本身免费开源') {
    throw 'Guide page must carry the optional author-shop card: the link comes from the user settings ShopUrl field (never hard-coded in XAML) and the card must say the software itself stays free.'
}
# 使用声明卡片（与仓库 DISCLAIMER.md 同义）：免责要点必须出现在指南页。
if ($guideMarkup -notmatch 'guide-disclaimer' -or
    $guideMarkup -notmatch '使用声明' -or
    $guideMarkup -notmatch '非官方' -or
    $guideMarkup -notmatch '不修改游戏' -or
    $guideMarkup -notmatch '可能出错' -or
    $guideMarkup -notmatch '费用自理' -or
    $guideMarkup -notmatch '不外发数据' -or
    $shopSource -notmatch '不买也能用全部功能') {
    throw 'Guide page must carry the usage disclaimer (非官方 / 不修改游戏 / 可能出错 / 费用自理 / 不外发数据) and the shop card must state that everything works without buying.'
}
if ($guideMarkup -notmatch '翻译服务配置' -or
    $guideMarkup -notmatch 'guide-tencent-console' -or
    $guideMarkup -notmatch 'guide-tencent-keys' -or
    $guideMarkup -notmatch 'guide-aliyun-console' -or
    $guideMarkup -notmatch 'guide-aliyun-keys' -or
    $guideMarkup -notmatch 'guide-deepseek-console' -or
    $guideMarkup -notmatch 'guide-xiaomi-console' -or
    $guideMarkup -notmatch '以官网为准' -or
    $guideMarkup -match 'AKID|LTAI|sk-[A-Za-z0-9]{10,}') {
    throw 'Guide page must carry the translation-service card: 开通 + 创建密钥 links with AutomationIds for every shipped provider, quota hints marked 以官网为准, and no key or example key material.'
}
# 配置简化 A：档案对话框提供自愿的「测试连接」（含错误码中文映射），
# 保存永远不被测试结果门禁（对话框不得出现“测试通过才能保存”类逻辑）。
$connectionTesterCode = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Infrastructure\Translation\TranslationConnectionTester.cs')
if ($translationCode -notmatch '测试连接' -or
    $translationCode -notmatch '指南 → 翻译服务配置' -or
    $translationCode -match '测试[^。]*才能保存' -or
    $connectionTesterCode -notmatch 'AuthFailure\.SecretIdNotFound' -or
    $connectionTesterCode -notmatch 'InvalidAccessKeyId' -or
    $connectionTesterCode -notmatch '网络不通' -or
    $connectionTesterCode -notmatch '"返回 402"' -or
    $connectionTesterCode -notmatch '"返回 403"') {
    throw 'Profile dialog must offer an opt-in 测试连接 with provider error hints; saving must never depend on the test outcome.'
}
# 配置简化 B：表单分层——接口地址 / 区域收进默认折叠的高级 Expander；
# 阿里云版本写 Options["scene"] **并且**写进 Model（两处必须一致，卡片与 Scene 才不会打架）；
# 腾讯隐藏模型但保存 TextTranslate；档案名防重名。
# 密钥粘贴自动拆分按用户决定**已移除**：代码里不得再出现自动拆分。
if ($translationCode -notmatch 'Expander' -or
    $translationCode -notmatch 'options\["scene"\]' -or
    $translationCode -notmatch '"tencent" => "TextTranslate"' -or
    $translationCode -notmatch '"aliyun" => \(aliyunSceneBox\.SelectedItem as ComboBoxItem\)' -or
    $translationCode -notmatch 'DedupeProfileName' -or
    $translationCode -match 'TrySplitPastedCredential|自动拆分') {
    throw 'Profile dialog must fold endpoint/region into a collapsed advanced expander, save the Aliyun edition into both Model and Options["scene"], keep tencent TextTranslate explicit, dedupe names, and NOT auto-split pasted keys.'
}
# 阿里云适配器：Scene 必须能由 Model 兜底（只存了 Model 的档案也要按所选版本调用），并做成可测方法。
$aliyunSource = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Infrastructure\Translation\TencentAliyunTranslationProviders.cs')
if ($aliyunSource -notmatch 'internal static string ResolveScene' -or
    $aliyunSource -notmatch 'internal static bool IsProfessional' -or
    $aliyunSource -notmatch 'professional \? "Translate" : "TranslateGeneral"' -or
    $aliyunSource -notmatch '\["Scene"\] = professional') {
    throw 'Aliyun provider must resolve the edition from the profile, and the professional edition must switch BOTH the Action (Translate) and the Scene (domain) - it is not just another general-version scene.'
}
# 实测出来的两个必填参数（都是"少了它整条链路必然失败"级别）：
#  · 腾讯 TMT 要求请求体带 ProjectId，否则返回 SignatureFailure（签名覆盖 body）
#  · 阿里云 TranslateGeneral 要求 FormatType，否则 400 MissingFormatType
if ($aliyunSource -notmatch '\["ProjectId"\] = int\.TryParse' -or
    $aliyunSource -notmatch '\["FormatType"\] = ') {
    throw 'Tencent must always send ProjectId and Aliyun must always send FormatType; both parameters are mandatory and their absence fails the whole call.'
}
$previewHandlerMatch = [regex]::Match(
    $mainWindowSource,
    '(?s)private\s+void\s+OnTranslationPreviewChanged\s*\([^)]*\)\s*\{(?<body>.*?)(?=\r?\n\s*private\s+)')
if (-not $previewHandlerMatch.Success) {
    throw 'MainWindow must retain the translation-preview event handler.'
}
$previewHandlerBody = $previewHandlerMatch.Groups['body'].Value
# Keep the WinUI access inside a local action, then invoke that action either
# directly on the UI thread or through the dispatcher. Merely mentioning a
# dispatcher elsewhere in MainWindow must not satisfy this regression guard.
if ($previewHandlerBody -notmatch '(?s)void\s+UpdatePreview\s*\(\s*\)\s*\{.*?OverlayWindowHost\.QuickInput.*?SetPreview\s*\(.*?\}\s*if\s*\(\s*DispatcherQueue\.HasThreadAccess\s*\)\s*\{\s*UpdatePreview\s*\(\s*\)\s*;\s*\}\s*else\s*\{\s*DispatcherQueue\.TryEnqueue\s*\(\s*UpdatePreview\s*\)\s*;') {
    throw 'MainWindow preview updates must marshal background translation events onto the UI dispatcher.'
}
if ((Get-Content -Raw $PSCommandPath) -notmatch 'Get-ConfiguredGlobalHotkey' -or (Get-Content -Raw $PSCommandPath) -notmatch 'Invoke-TestHotkey') { throw 'Subtitle shortcut smoke must read and exercise the persisted VoiceHotkey instead of hard-coding F7.' }
if ($mainWindowSource -notmatch 'LastSecondaryTranslatedText' -or $mainWindowSource -notmatch 'SetPreview\(\s*state\.LastOriginalText\s*,\s*state\.LastTranslatedText\s*,\s*state\.LastSecondaryTranslatedText') { throw 'Global quick-input entry must preserve the optional second translation in its preview.' }
if ($translationCode -notmatch 'RouteChanged\s*\+=\s*OnRouteChanged' -or $translationCode -notmatch 'RouteChanged\s*-\=\s*OnRouteChanged' -or $translationCode -notmatch 'DispatcherQueue\.TryEnqueue') { throw 'Translation settings must refresh after an asynchronously restored route without leaking page handlers.' }
if ($mainWindowMarkup -notmatch 'logo-mark\.png' -or $mainWindowSource -notmatch 'SetIcon\(iconPath\)' -or $mainWindowSource -notmatch 'Activated\s*\+=\s*OnWindowActivated') { throw 'The desktop shell must include visible branding, a title-bar icon, and post-activation window setup.' }
if ($mainWindowSource -notmatch 'Math\.Max\(960' -or $mainWindowSource -notmatch 'Math\.Min\(1480' -or $mainWindowSource -notmatch 'appWindow\.Resize') { throw 'The desktop shell must retain bounded DPI-safe window sizing.' }
foreach ($navId in @('run', 'input', 'voice', 'translation', 'settings', 'guide')) {
    $navAutomationId = 'AutomationProperties.AutomationId="nav-' + $navId + '"'
    if ($mainWindowMarkup -notmatch [regex]::Escape($navAutomationId)) { throw "The desktop shell is missing the '$navId' navigation automation id." }
}
if ($mainWindowSource -notmatch '"input"\s*=>\s*\(.*输入' -or $mainWindowSource -notmatch '"voice"\s*=>\s*\(.*语音') { throw 'The desktop navigation must use the concise 输入 and 语音 labels.' }
if ($settingsMarkup -match 'Text="快捷输入"' -or (Get-Content -Raw $guidePage) -match 'Text="快捷输入"') { throw 'User-facing shortcut labels must not use the obsolete 快捷输入 name.' }
if ((Get-Content -Raw $guidePage) -notmatch 'Text="打开输入框"' -or $inputMarkup -notmatch 'Content="打开输入框"' -or $runMarkup -notmatch 'Text="输入"') { throw 'The input action must use the concise 输入 page label and the unified 打开输入框 action.' }
if ($mainWindowMarkup -match 'Background="#24314D"|BorderBrush="#5278CF"') { throw 'The sidebar brand icon must keep its transparent artwork without an opaque decorative frame.' }
if ((Get-Content -Raw $desktopProject) -notmatch 'logo-mark\.png') { throw 'The sidebar brand image must be copied with the desktop output.' }
$pageMarkupFiles = Get-ChildItem (Join-Path $desktopSource 'Pages') -File -Filter *.xaml | Where-Object { $_.Name -ne 'SubtitleOverlayWindow.xaml' }
foreach ($pageMarkupFile in $pageMarkupFiles) {
    $pageMarkup = Get-Content -Raw -LiteralPath $pageMarkupFile.FullName
    if ($pageMarkup -notmatch 'HorizontalScrollBarVisibility="Disabled"' -or $pageMarkup -notmatch 'HorizontalAlignment="Stretch"') {
        throw "Page '$($pageMarkupFile.Name)' must stretch within the visible viewport without a horizontal scroll path."
    }
}
$visibleXamlFiles = Get-ChildItem $desktopSource -Recurse -File -Filter *.xaml | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$visibleXaml = ($visibleXamlFiles | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }) -join "`n"
if ($visibleXaml -match '>V2<|工作区 /|翻译档案|Windows 原生翻译工作台') { throw 'Internal architecture labels must not be visible in the desktop UI.' }
if ($visibleXaml -match 'Text="(?:自身输入|他人语音字幕)"') { throw 'Desktop page labels must use the concise 输入 and 字幕 names.' }
if ($visibleXaml -match '翻译设置') { throw 'The old duplicate translation-settings label must not remain in the desktop UI.' }
if ($visibleXaml -match 'gpt-4o-mini') { throw 'A provider-specific model must not be hard-coded into the desktop UI.' }
if ($visibleXaml -notmatch 'TermSourceBox|术语库') { throw 'The translation page must expose an editable glossary.' }

function Wait-JsonDocument {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][scriptblock] $Predicate,
        [Parameter(Mandatory)][string] $Failure,
        [int] $Attempts = 40
    )

    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        try {
            if (Test-Path -LiteralPath $Path) {
                $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
                if (& $Predicate $document) { return }
            }
        }
        catch {
            # The atomic replace can be observed mid-write; retry.
        }
        Start-Sleep -Milliseconds 150
    }
    throw $Failure
}

function Find-GlossaryRowDeleteButton {
    param(
        [Parameter(Mandatory)] $Window,
        [Parameter(Mandatory)][string] $RowText
    )

    $row = $Window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $RowText)))
    if ($null -eq $row) { return $null }

    $deleteCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'glossary-delete')
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $node = $row
    while ($null -ne $node) {
        $button = $node.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $deleteCondition)
        if ($null -ne $button) { return $button }
        $node = $walker.GetParent($node)
    }
    return $null
}

function Invoke-TranslationUiSmoke {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Remove-Item $startupLog -ErrorAction SilentlyContinue
    $env:VRC_TRANSLATE_SMOKE_PAGE = 'run'
    # The glossary writes itself now, so this smoke checks the real document and
    # restores it afterwards.
    $glossaryPath = Join-Path $desktopOutput 'data\glossary.json'
    $glossarySnapshot = if (Test-Path -LiteralPath $glossaryPath) { Get-Content -LiteralPath $glossaryPath -Raw } else { $null }
    $process = $null
    try {
        $process = Start-Process -FilePath $executable -WorkingDirectory $desktopOutput -PassThru
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $window = $null
        for ($attempt = 0; $attempt -lt 30 -and $null -eq $window; $attempt++) {
            Start-Sleep -Milliseconds 200
            $window = @($root.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.Condition]::TrueCondition)) |
                Where-Object { $_.Current.Name -eq 'VRCTranslate' -and $_.Current.ProcessId -eq $process.Id } |
                Select-Object -First 1
        }
        if ($null -eq $window) { throw 'UI smoke could not find the VRCTranslate window.' }

        # Guard the exact shell regressions reported during manual review:
        # the brand image must be materialized, and the navigation must expose
        # one translation entry with the concise page title.
        $brandCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'VRCTranslate 图标')
        $brandImage = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $brandCondition)
        if ($null -eq $brandImage -or $brandImage.Current.BoundingRectangle.Width -lt 16 -or $brandImage.Current.BoundingRectangle.Height -lt 16) {
            throw 'The visible VRCTranslate brand icon is missing or has a zero-sized layout.'
        }

        $translationNavCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'nav-translation')
        $translationNavButtons = @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $translationNavCondition))
        if ($translationNavButtons.Count -ne 1) { throw "Expected one translation navigation entry, found $($translationNavButtons.Count)." }

        # The run-page service entry was the previous manual crash path. Open
        # and cancel it once before navigating away so the release smoke covers
        # the real user entry point as well as the profile editor.
        $configureCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'route-summary-config')
        $configureButton = $null
        for ($attempt = 0; $attempt -lt 30 -and $null -eq $configureButton; $attempt++) {
            Start-Sleep -Milliseconds 200
            $configureButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $configureCondition)
        }
        if ($null -eq $configureButton) {
            # Older builds exposed the same entry by its visible text only;
            # keep the smoke tolerant while the stable AutomationId remains
            # the preferred route.
            $fallbackCondition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, '配置服务')
            $configureButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $fallbackCondition)
        }
        if ($null -eq $configureButton) { throw 'Run page does not expose the translation service configuration entry.' }
        $configureButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        # The current shell opens the dedicated translation page directly;
        # older builds opened a small service dialog first. Accept either
        # route so this smoke test follows the actual user entry point.
        $serviceDialogCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '翻译服务')
        $serviceDialog = $null
        for ($attempt = 0; $attempt -lt 12 -and $null -eq $serviceDialog; $attempt++) {
            Start-Sleep -Milliseconds 150
            $serviceDialog = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $serviceDialogCondition)
        }
        if ($null -ne $serviceDialog) {
            $serviceCancel = $serviceDialog.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, '取消')))
            if ($null -ne $serviceCancel) {
                $serviceCancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            } else {
                try { $serviceDialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
            }
        }
        $profileAddCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'translation-profile-add')
        $profileAdd = $null
        for ($attempt = 0; $attempt -lt 20 -and $null -eq $profileAdd; $attempt++) {
            Start-Sleep -Milliseconds 150
            $profileAdd = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $profileAddCondition)
        }
        if ($null -eq $profileAdd) { throw 'Opening translation service configuration did not expose the translation profile page.' }
        Start-Sleep -Milliseconds 200
        if ($process.HasExited) { throw "Opening translation service configuration terminated the desktop process with code $($process.ExitCode)." }

        $navigationButton = $translationNavButtons[0]
        if ($null -eq $navigationButton) { throw 'UI smoke could not find the translation navigation button.' }
        $navigationInvoke = $navigationButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $navigationInvoke.Invoke()
        Start-Sleep -Milliseconds 700

        # The profile file is user-editable, so the first service is not
        # guaranteed to be OpenAI. Wait for any restored profile card and use
        # its concrete automation id instead of assuming a provider.
        $editButton = $null
        $profileEdits = @()
        $profileDeletes = @()
        $currentCard = $null
        $windowRect = $window.Current.BoundingRectangle
        for ($attempt = 0; $attempt -lt 40 -and $null -eq $editButton; $attempt++) {
            Start-Sleep -Milliseconds 200
            $elements = @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
            $profileEdits = @($elements | Where-Object { $_.Current.AutomationId -match '^edit-.+' })
            $profileDeletes = @($elements | Where-Object { $_.Current.AutomationId -match '^delete-.+' })
            $currentCards = @($elements | Where-Object { $_.Current.Name -match '当前' })
            $editButton = $profileEdits | Where-Object {
                $rect = $_.Current.BoundingRectangle
                $rect.Width -gt 0 -and $rect.Height -gt 0 -and
                -not [double]::IsInfinity($rect.Width) -and -not [double]::IsInfinity($rect.Height)
            } | Select-Object -First 1
            $currentCard = $currentCards | Where-Object {
                $rect = $_.Current.BoundingRectangle
                $rect.Width -gt 0 -and $rect.Height -gt 0 -and
                -not [double]::IsInfinity($rect.Width) -and -not [double]::IsInfinity($rect.Height)
            } | Select-Object -First 1
        }
        if ($null -eq $editButton) { throw 'UI smoke could not find a visible translation service edit button after profile restore.' }
        if ($null -eq $currentCard) { throw 'UI smoke could not find the highlighted current translation service card.' }
        $currentCardRect = $currentCard.Current.BoundingRectangle
        if ($currentCardRect.Left -lt $windowRect.Left - 2 -or $currentCardRect.Right -gt $windowRect.Right + 2) {
            throw 'The highlighted translation service card extends outside the application window.'
        }
        foreach ($profileButton in @($profileEdits) + @($profileDeletes)) {
            $rect = $profileButton.Current.BoundingRectangle
            if ($rect.Width -gt 0 -and $rect.Height -gt 0 -and
                -not [double]::IsInfinity($rect.Width) -and -not [double]::IsInfinity($rect.Height) -and
                ($rect.Left -lt $windowRect.Left - 2 -or $rect.Right -gt $windowRect.Right + 2)) {
                throw "Translation profile action '$($profileButton.Current.AutomationId)' is outside the application window."
            }
        }
        $invokePattern = $editButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invokePattern.Invoke()
        $dialogCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '编辑翻译服务档案')
        $profileDialog = $null
        for ($attempt = 0; $attempt -lt 30 -and $null -eq $profileDialog; $attempt++) {
            Start-Sleep -Milliseconds 200
            $profileDialog = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $dialogCondition)
        }
        if ($null -eq $profileDialog) { throw 'Editing a translation profile did not open the profile dialog.' }
        $cancelButton = $profileDialog.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, '取消')))
        if ($null -ne $cancelButton) {
            $cancelButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        } else {
            try { $profileDialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
        }
        Start-Sleep -Milliseconds 300

        $deleteCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'glossary-delete')
        $deleteButtons = @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $deleteCondition))
        if ($deleteButtons.Count -lt 4) { throw 'The default glossary entries were not rendered.' }
        foreach ($deleteButton in $deleteButtons) {
            $rect = $deleteButton.Current.BoundingRectangle
            if ($rect.Width -gt 0 -and $rect.Height -gt 0 -and
                -not [double]::IsInfinity($rect.Width) -and -not [double]::IsInfinity($rect.Height) -and
                ($rect.Left -lt $windowRect.Left - 2 -or $rect.Right -gt $windowRect.Right + 2)) {
                throw 'A glossary delete button is outside the application window.'
            }
        }

        # 术语库没有保存按钮了：用一个一次性术语证明修改、新增、删除和启用开关
        # 都立刻写进了磁盘文件，而不是"看起来保存了"。
        $probeTarget = 'V2-验证-目标-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        $probeTerm = 'V2-验证-词条-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        $firstDeleteButton = $deleteButtons |
            Where-Object { $_.Current.BoundingRectangle.Width -gt 0 } |
            Select-Object -First 1
        if ($null -eq $firstDeleteButton) { throw 'UI smoke could not find a rendered glossary row to edit.' }
        $firstRow = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($firstDeleteButton)
        $firstRowText = $firstRow.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Text)))
        if ($null -eq $firstRowText -or [string]::IsNullOrWhiteSpace($firstRowText.Current.Name)) {
            throw 'UI smoke could not read the glossary row that is about to be edited.'
        }

        $termSource = $window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'TermSourceBox')))
        $termTarget = $window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'TermTargetBox')))
        $addTerm = $window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'glossary-add')))
        if ($null -eq $termSource -or $null -eq $termTarget -or $null -eq $addTerm) {
            throw 'UI smoke could not find the glossary editors.'
        }
        $sourceValue = $termSource.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $targetValue = $termTarget.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $addInvoke = $addTerm.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)

        # 修改：把已渲染术语的译词改成探针值。
        $sourceValue.SetValue($firstRowText.Current.Name)
        $targetValue.SetValue($probeTarget)
        $addInvoke.Invoke()
        Wait-JsonDocument -Path $glossaryPath -Failure "Editing a glossary term did not persist '$probeTarget' into $glossaryPath; the removed save button must be replaced by an immediate write." -Predicate {
            param($document)
            @($document.entries) | Where-Object { $_.target -eq $probeTarget } | Select-Object -First 1
        }

        # 新增：新词条必须同样立即落盘。
        $sourceValue.SetValue($probeTerm)
        $targetValue.SetValue($probeTerm)
        $addInvoke.Invoke()
        Wait-JsonDocument -Path $glossaryPath -Failure "Adding a glossary term did not persist '$probeTerm' into $glossaryPath." -Predicate {
            param($document)
            @($document.entries) | Where-Object { $_.source -eq $probeTerm } | Select-Object -First 1
        }

        # 删除：删掉刚才修改的那一条，文件里不能留下探针值。
        $probeDeleteButton = Find-GlossaryRowDeleteButton -Window $window -RowText $probeTarget
        if ($null -eq $probeDeleteButton) { throw 'UI smoke could not find the glossary row it just edited.' }
        $probeDeleteButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-JsonDocument -Path $glossaryPath -Failure "Deleting a glossary term left '$probeTarget' in $glossaryPath." -Predicate {
            param($document)
            -not (@($document.entries) | Where-Object { $_.target -eq $probeTarget })
        }

        # 启用开关：原来的保存按钮也会写 Enabled，所以开关同样必须自己落盘。
        $glossarySwitch = $window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'glossary-enabled')))
        if ($null -eq $glossarySwitch) { throw 'UI smoke could not find the glossary enable switch.' }
        $glossaryToggle = $glossarySwitch.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $wasEnabled = $glossaryToggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
        $glossaryToggle.Toggle()
        Wait-JsonDocument -Path $glossaryPath -Failure "Turning the glossary switch off did not persist Enabled=false into $glossaryPath." -Predicate {
            param($document)
            [bool]$document.enabled -ne $wasEnabled
        }
        $glossaryToggle.Toggle()
        Wait-JsonDocument -Path $glossaryPath -Failure "Turning the glossary switch back did not persist Enabled=$wasEnabled into $glossaryPath." -Predicate {
            param($document)
            [bool]$document.enabled -eq $wasEnabled
        }

        Write-Host '  Translation UI interaction smoke passed.'
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        # 恢复验证前的术语库文件：验证本身不改变本机数据目录。
        try {
            if ($null -ne $glossarySnapshot) {
                [System.IO.File]::WriteAllText($glossaryPath, $glossarySnapshot, (New-Object System.Text.UTF8Encoding($false)))
            }
            elseif (Test-Path -LiteralPath $glossaryPath) {
                Remove-Item -LiteralPath $glossaryPath -Force
            }
        }
        catch { }
    }
    if (Test-Path $startupLog) {
        throw "Translation UI smoke wrote a startup failure log:`n$(Get-Content $startupLog -Raw)"
    }
}

function Invoke-DesktopSmoke([string] $pageName) {
    Remove-Item $startupLog -ErrorAction SilentlyContinue
    $env:VRC_TRANSLATE_SMOKE_PAGE = $pageName
    $process = $null
    try {
        $process = Start-Process -FilePath $executable -WorkingDirectory $desktopOutput -PassThru
        Start-Sleep -Seconds 5
        if ($process.HasExited) {
            throw "Desktop smoke test '$pageName' exited with code $($process.ExitCode)."
        }
        Write-Host "  '$pageName' page stayed alive for 5 seconds."
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
        Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
        # WinUI/Windows App SDK tears down its bootstrap process asynchronously.
        # Give the runtime a moment before launching the next isolated page smoke.
        Start-Sleep -Seconds 2
    }
    # WinUI teardown is asynchronous. Poll briefly so a normal exit is not
    # reported as a leaked process on the first enumeration after Stop-Process.
    $remaining = @()
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $remaining = @(Get-Process -Name VrcTranslate -ErrorAction SilentlyContinue | Where-Object { $_.HandleCount -gt 0 })
        if ($remaining.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($remaining.Count -gt 0) {
        $ids = $remaining.Id -join ', '
        throw "Desktop smoke test '$pageName' left VrcTranslate.exe process(es) running: $ids."
    }
    if (Test-Path $startupLog) {
        $details = Get-Content $startupLog -Raw
        throw "Desktop smoke test '$pageName' wrote a startup failure log:`n$details"
    }
}

function Get-ConfiguredGlobalHotkey([string] $propertyName, [string] $fallback) {
    $settingsPath = Join-Path $desktopOutput 'data\v2-user-settings.json'
    if (Test-Path -LiteralPath $settingsPath) {
        try {
            $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            $value = [string]$settings.$propertyName
            if (-not [string]::IsNullOrWhiteSpace($value)) { return $value.Trim() }
        }
        catch {
            Write-Host "  Ignoring unreadable hotkey settings for $propertyName; using default." -ForegroundColor Yellow
        }
    }
    return $fallback
}

function Invoke-TestHotkey([string] $gesture) {
    if ([string]::IsNullOrWhiteSpace($gesture)) { throw 'Hotkey gesture is required.' }
    $parts = $gesture.ToUpperInvariant().Replace(' ', '').Split('+') | Where-Object { $_ }
    $modifiers = @()
    $virtualKey = 0
    foreach ($part in $parts) {
        switch ($part) {
            'CTRL' { $modifiers += 0x11; continue }
            'CONTROL' { $modifiers += 0x11; continue }
            'ALT' { $modifiers += 0x12; continue }
            'MENU' { $modifiers += 0x12; continue }
            'SHIFT' { $modifiers += 0x10; continue }
            'WIN' { $modifiers += 0x5B; continue }
            'WINDOWS' { $modifiers += 0x5B; continue }
            default {
                if ($part.Length -eq 1 -and $part -match '[A-Z0-9]') {
                    $virtualKey = [byte][char]$part[0]
                }
                elseif ($part -match '^F([1-9]|1[0-2])$') {
                    $virtualKey = 0x6F + [int]$Matches[1]
                }
                else { throw "Unsupported hotkey gesture: $gesture" }
            }
        }
    }
    if ($virtualKey -eq 0) { throw "Unsupported hotkey gesture: $gesture" }
    foreach ($modifier in $modifiers) { [VrcTranslateValidationNative]::KeybdEvent($modifier, 0, 0, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 120
    [VrcTranslateValidationNative]::KeybdEvent($virtualKey, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 180
    [VrcTranslateValidationNative]::KeybdEvent($virtualKey, 0, 2, [UIntPtr]::Zero)
    for ($index = $modifiers.Count - 1; $index -ge 0; $index--) {
        [VrcTranslateValidationNative]::KeybdEvent($modifiers[$index], 0, 2, [UIntPtr]::Zero)
    }
}

function Invoke-VoiceUiSmoke {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Remove-Item $startupLog -ErrorAction SilentlyContinue
    $env:VRC_TRANSLATE_SMOKE_PAGE = 'voice'
    # The caption content option is persisted here; restore it after the smoke.
    $voiceSettingsPath = Join-Path $desktopOutput 'data\v2-voice-settings.json'
    $voiceSettingsSnapshot = if (Test-Path -LiteralPath $voiceSettingsPath) { Get-Content -LiteralPath $voiceSettingsPath -Raw } else { $null }
    $process = $null
    try {
        $process = Start-Process -FilePath $executable -WorkingDirectory $desktopOutput -PassThru
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $window = $null
        for ($attempt = 0; $attempt -lt 30 -and $null -eq $window; $attempt++) {
            Start-Sleep -Milliseconds 200
            $window = @($root.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.Condition]::TrueCondition)) |
                Where-Object { $_.Current.Name -eq 'VRCTranslate' -and $_.Current.ProcessId -eq $process.Id } |
                Select-Object -First 1
        }
        if ($null -eq $window) { throw 'Voice UI smoke could not find the VRCTranslate window.' }

        # D3：语音页不再有「打开字幕」按钮——浮窗只随他人语音识别出现与消失，
        # 页面上只保留这一个开始 / 停止动作。
        $windowOnlyEntry = $window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, '打开字幕')))
        if ($null -ne $windowOnlyEntry) { throw 'Voice page still exposes the retired 打开字幕 button.' }
        $startButton = $window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                'voice-start')))
        if ($null -eq $startButton) {
            $startButton = $window.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty,
                    '开始识别')))
        }
        if ($null -eq $startButton) { throw 'Voice page does not expose the recognition action.' }
        $startButtonRect = $startButton.Current.BoundingRectangle
        $mainRect = $window.Current.BoundingRectangle
        if ($startButtonRect.Width -lt 60 -or $startButtonRect.Height -lt 24 -or
            $startButtonRect.Left -lt ($mainRect.Left - 2) -or $startButtonRect.Right -gt ($mainRect.Right + 2) -or
            $startButtonRect.Top -lt ($mainRect.Top - 2) -or $startButtonRect.Bottom -gt ($mainRect.Bottom + 2)) {
            throw 'Voice page recognition action is clipped or unusable.'
        }
        $hotkeySummaryCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'voice-hotkey-summary')
        $hotkeySummary = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $hotkeySummaryCondition)
        if ($null -eq $hotkeySummary) {
            $hotkeySummary = $window.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, 'F7')))
        }
        if ($null -eq $hotkeySummary) { throw 'Voice page does not display the configured 他人语音 shortcut.' }
        if ($null -eq ('VrcTranslateValidationNative' -as [type])) {
            Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VrcTranslateValidationNative {
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    public static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
'@
        }

        function Find-CaptionWindow {
            $candidateWindows = @($root.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Window))))
            return $candidateWindows | Where-Object {
                $_.Current.ProcessId -eq $process.Id -and
                $_.Current.NativeWindowHandle -ne $window.Current.NativeWindowHandle -and
                $null -ne $_.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                        'subtitle-text')))
            } | Select-Object -First 1
        }

        function Get-VoiceStatusName {
            $statusElement = $window.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                    'VoiceStatusText')))
            if ($null -eq $statusElement) { return '' }
            return $statusElement.Current.Name
        }

        # Read the same persisted shortcut the application reads. This keeps the
        # smoke test valid after a user changes the global binding.
        $configuredVoiceHotkey = Get-ConfiguredGlobalHotkey 'VoiceHotkey' 'F7'

        # 识别没跑的时候浮窗不该留在屏幕上——这正是「浮窗只随识别状态出现与消失」。
        $preRunOverlay = Find-CaptionWindow
        if ($null -ne $preRunOverlay -and
            [VrcTranslateValidationNative]::IsWindowVisible([IntPtr]$preRunOverlay.Current.NativeWindowHandle)) {
            throw 'The caption surface must not be on screen before other-player recognition starts.'
        }

        # 一次「他人语音」＝开始识别 + 显示浮窗。
        Invoke-TestHotkey $configuredVoiceHotkey
        $overlay = $null
        for ($attempt = 0; $attempt -lt 60 -and $null -eq $overlay; $attempt++) {
            Start-Sleep -Milliseconds 200
            $candidate = Find-CaptionWindow
            if ($null -ne $candidate -and
                [VrcTranslateValidationNative]::IsWindowVisible([IntPtr]$candidate.Current.NativeWindowHandle)) {
                $overlay = $candidate
            }
        }
        if ($null -eq $overlay) { throw 'The 他人语音 shortcut did not start recognition and show the caption surface.' }
        $overlayControls = @($overlay.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition))
        if ($overlayControls | Where-Object { $_.Current.Name -in @('识别语言', '目标语言', '显示方式', '他人语音字幕', '等待') }) {
            throw 'The subtitle overlay still exposes redundant title, language, display, or status controls.'
        }
        $subtitleText = $overlayControls | Where-Object { $_.Current.AutomationId -eq 'subtitle-text' } | Select-Object -First 1
        # The surface carries messages only: it opens empty and fills up as
        # sentences arrive, because recognition just started.
        if ($null -ne $subtitleText -and -not [string]::IsNullOrEmpty($subtitleText.Current.Name)) {
            throw "The caption surface must open empty; received '$($subtitleText.Current.Name)'."
        }
        $overlayRect = $overlay.Current.BoundingRectangle
        Assert-OverlayBounds -Bounds $overlayRect -ConfiguredSize $subtitleOverlayDefaultSize -Label 'Subtitle'
        $overlayStyle = [VrcTranslateValidationNative]::GetWindowLongPtr(
            [IntPtr]$overlay.Current.NativeWindowHandle, -16).ToInt64()
        if (($overlayStyle -band 0x00C00000) -ne 0x00C00000) {
            throw 'The subtitle overlay lost its standard Windows caption.'
        }
        if (($overlayStyle -band 0x00040000) -eq 0) {
            throw 'The subtitle overlay lost the standard Windows resize frame.'
        }
        $overlayHandle = [IntPtr]$overlay.Current.NativeWindowHandle
        if (-not [VrcTranslateValidationNative]::IsWindowVisible($overlayHandle)) {
            throw 'The caption surface must be visible while recognition is running.'
        }
        # 浮窗在屏幕上时识别必须真的在跑：窗口状态和识别状态来自同一个串行化开关。
        $runningStatus = Get-VoiceStatusName
        if ($runningStatus -notmatch '识别中') {
            throw "The caption surface is visible while the voice page reports '$runningStatus'."
        }

        # 再按一次＝停止识别 + 隐藏浮窗，两个状态必须一起翻转。
        Invoke-TestHotkey $configuredVoiceHotkey
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            Start-Sleep -Milliseconds 200
            if (-not [VrcTranslateValidationNative]::IsWindowVisible($overlayHandle) -and (Get-VoiceStatusName) -match '停止') { break }
        }
        if ([VrcTranslateValidationNative]::IsWindowVisible($overlayHandle)) {
            throw 'The 他人语音 shortcut did not hide the caption surface on its second press.'
        }
        $stoppedStatus = Get-VoiceStatusName
        if ($stoppedStatus -notmatch '停止') {
            throw "The caption surface was hidden while the voice page still reports '$stoppedStatus'."
        }

        # 第三次＝重新开始识别并显示浮窗，总开关可以反复切换。
        Invoke-TestHotkey $configuredVoiceHotkey
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            Start-Sleep -Milliseconds 200
            if ([VrcTranslateValidationNative]::IsWindowVisible($overlayHandle) -and (Get-VoiceStatusName) -match '识别中') { break }
        }
        if (-not [VrcTranslateValidationNative]::IsWindowVisible($overlayHandle)) {
            throw 'The 他人语音 shortcut did not show the caption surface again.'
        }
        $restartedStatus = Get-VoiceStatusName
        if ($restartedStatus -notmatch '识别中') {
            throw "The caption surface reappeared while the voice page reports '$restartedStatus'."
        }
        # D1：字幕内容选项（仅译文 / 译文 + 原文）持久化在 v2-voice-settings.json。
        # 切换后必须立刻写入，重新进入语音页时必须从文件读回，切回原值也要落盘。
        $contentCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'subtitle-content-toggle')
        $contentToggle = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $contentCondition)
        if ($null -eq $contentToggle) { throw 'Voice page does not expose the caption content option.' }
        $contentPattern = $contentToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $wasFullCaption = $contentPattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
        $expectedTag = if ($wasFullCaption) { 'translated-only' } else { 'translated-with-original' }
        $contentPattern.Toggle()
        Wait-JsonDocument -Path $voiceSettingsPath -Failure "Switching the caption content option did not persist '$expectedTag' into $voiceSettingsPath." -Predicate {
            param($document)
            [string]$document.SubtitleContent -eq $expectedTag
        }

        # 读回验证：重新进入语音页，开关状态必须来自刚写入的文件。
        foreach ($navigationId in @('nav-run', 'nav-voice')) {
            $navigationButton = $window.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $navigationId)))
            if ($null -eq $navigationButton) { throw "Voice smoke could not find the '$navigationId' navigation entry." }
            $navigationButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 300
        }
        $expectedState = if ($wasFullCaption) { [System.Windows.Automation.ToggleState]::Off } else { [System.Windows.Automation.ToggleState]::On }
        $restoredState = $null
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            Start-Sleep -Milliseconds 150
            $reloadedToggle = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $contentCondition)
            if ($null -eq $reloadedToggle) { continue }
            try {
                $restoredState = $reloadedToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
            }
            catch {
                continue
            }
            if ($restoredState -eq $expectedState) { break }
        }
        if ($restoredState -ne $expectedState) {
            throw "Reopening the voice page did not restore the caption content option from $voiceSettingsPath (state '$restoredState')."
        }

        $reloadedToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        $restoredTag = if ($wasFullCaption) { 'translated-with-original' } else { 'translated-only' }
        Wait-JsonDocument -Path $voiceSettingsPath -Failure "Switching the caption content option back did not persist '$restoredTag' into $voiceSettingsPath." -Predicate {
            param($document)
            [string]$document.SubtitleContent -eq $restoredTag
        }

        # 关闭字幕窗口＝关掉整个他人语音：窗口消失时识别不能还在跑（D3.5）。
        $closableOverlay = Find-CaptionWindow
        if ($null -eq $closableOverlay) { throw 'The caption surface is not on screen to close.' }
        $closableOverlay.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            Start-Sleep -Milliseconds 200
            if (-not [VrcTranslateValidationNative]::IsWindowVisible($overlayHandle) -and (Get-VoiceStatusName) -match '停止') { break }
        }
        if ([VrcTranslateValidationNative]::IsWindowVisible($overlayHandle)) {
            throw 'Closing the caption window did not hide it.'
        }
        $closedStatus = Get-VoiceStatusName
        if ($closedStatus -notmatch '停止') {
            throw "Closing the caption window left the recognition session running ('$closedStatus')."
        }
        Write-Host '  Voice caption master-switch interaction smoke passed.'
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        # 恢复验证前的语音设置文件：验证本身不改变本机数据目录。
        try {
            if ($null -ne $voiceSettingsSnapshot) {
                [System.IO.File]::WriteAllText($voiceSettingsPath, $voiceSettingsSnapshot, (New-Object System.Text.UTF8Encoding($false)))
            }
            elseif (Test-Path -LiteralPath $voiceSettingsPath) {
                Remove-Item -LiteralPath $voiceSettingsPath -Force
            }
        }
        catch { }
    }
    if (Test-Path $startupLog) {
        throw "Voice UI smoke wrote a startup failure log:`n$(Get-Content $startupLog -Raw)"
    }
}

function Ensure-ValidationSmokeNative {
    # 专门的类型名：别的冒烟也会定义 VrcTranslateValidationNative，同一个会话里后定义的会直接失败。
    if ($null -ne ('VrcTranslateOscSmokeNative' -as [type])) { return }
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VrcTranslateOscSmokeNative {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    public static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
'@
}

function Receive-OscChatboxPacket {
    <#
    等一个 /chatbox/input 报文并返回它的字符串参数。这是应用真正写出的线格式，收到即证明
    VRChat 会看到什么。
    #>
    param(
        [Parameter(Mandatory)] $Client,
        [int] $TimeoutMs = 20000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Client.Client.ReceiveTimeout = [Math]::Max(250, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        try {
            $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
            $datagram = $Client.Receive([ref]$remote)
        }
        catch [System.Net.Sockets.SocketException] {
            continue
        }
        if ($null -eq $datagram -or $datagram.Length -lt 8) { continue }
        $addressEnd = [Array]::IndexOf($datagram, [byte]0)
        if ($addressEnd -lt 0) { continue }
        $address = [System.Text.Encoding]::ASCII.GetString($datagram, 0, $addressEnd)
        if ($address -ne '/chatbox/input') { continue }
        $offset = [int]([Math]::Ceiling(($addressEnd + 1) / 4.0) * 4)
        if ($offset -ge $datagram.Length) { continue }
        # 类型标签以 0 结尾、再补齐到 4 字节边界；字符串参数本身也是「0 结尾 + 补齐」，
        # 所以直接读到下一个 0 为止，不假设前面有长度字段。
        $tagsEnd = [Array]::IndexOf($datagram, [byte]0, $offset)
        if ($tagsEnd -lt 0) { continue }
        $valueStart = [int]([Math]::Ceiling(($tagsEnd + 1) / 4.0) * 4)
        if ($valueStart -ge $datagram.Length) { continue }
        $valueEnd = [Array]::IndexOf($datagram, [byte]0, $valueStart)
        if ($valueEnd -le $valueStart) { continue }
        return [System.Text.Encoding]::UTF8.GetString($datagram, $valueStart, $valueEnd - $valueStart)
    }

    return $null
}

function Set-QuickInputText {
    param(
        [Parameter(Mandatory)] $Root,
        [Parameter(Mandatory)] [int] $ProcessId,
        [Parameter(Mandatory)] [string] $Text
    )

    $textBox = $null
    for ($attempt = 0; $attempt -lt 30 -and $null -eq $textBox; $attempt++) {
        Start-Sleep -Milliseconds 200
        $textBox = @($Root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'quick-input-text'))) |
            Where-Object { $_.Current.ProcessId -eq $ProcessId }) | Select-Object -First 1
    }
    if ($null -eq $textBox) { throw 'OSC chatbox smoke could not find the quick-input text box.' }

    $valuePattern = $textBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue($Text)
    if ($valuePattern.Current.Value -ne $Text) {
        throw ('Quick-input did not accept the smoke text (got ' + $valuePattern.Current.Value + ').')
    }

    return $textBox
}

function Send-QuickInputText {
    param(
        [Parameter(Mandatory)] $TextBox,
        [Parameter(Mandatory)] [IntPtr] $WindowHandle
    )

    # 先把浮窗提到前台，再用 keybd_event 送一次 Enter：pwsh 里 WScript.Shell 是迟绑定 COM，
    # 拿不到 SendWait，而键盘事件只会送到前台窗口。焦点可能在窗口刚建好时又被拿走，所以确认一次。
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        [VrcTranslateOscSmokeNative]::SetForegroundWindow($WindowHandle) | Out-Null
        Start-Sleep -Milliseconds 300
        $TextBox.SetFocus()
        Start-Sleep -Milliseconds 200
        if ([VrcTranslateOscSmokeNative]::GetForegroundWindow() -eq $WindowHandle) { break }
    }
    [VrcTranslateOscSmokeNative]::KeybdEvent(0x0D, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [VrcTranslateOscSmokeNative]::KeybdEvent(0x0D, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

function Find-QuickInputWindow {
    param(
        [Parameter(Mandatory)] $Root,
        [Parameter(Mandatory)] [int] $ProcessId,
        [Parameter(Mandatory)] [int] $MainWindowHandle
    )

    return @($Root.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window)))) |
        Where-Object {
            $_.Current.ProcessId -eq $ProcessId -and
            $_.Current.NativeWindowHandle -ne $MainWindowHandle -and
            $null -ne $_.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                    'quick-input-text')))
        } | Select-Object -First 1
}

function Open-QuickInputWindow {
    param(
        [Parameter(Mandatory)] $Root,
        [Parameter(Mandatory)] [int] $ProcessId,
        [Parameter(Mandatory)] [int] $MainWindowHandle
    )

    # 输入浮窗随主窗口一起出现在桌面上，先找已经打开的那个；找不到再按导航页上的
    # 「打开输入框」——那个按钮是开关，窗口已经在屏幕上时按下去反而会关掉它。
    $inputWindow = $null
    for ($attempt = 0; $attempt -lt 15 -and $null -eq $inputWindow; $attempt++) {
        $inputWindow = Find-QuickInputWindow -Root $Root -ProcessId $ProcessId -MainWindowHandle $MainWindowHandle
        if ($null -eq $inputWindow) { Start-Sleep -Milliseconds 200 }
    }
    if ($null -ne $inputWindow) { return $inputWindow }

    $openCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, '打开输入框')
    $openButton = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $openCondition)
    if ($null -eq $openButton) { throw 'OSC chatbox smoke could not find the quick-input entry.' }
    $openButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    for ($attempt = 0; $attempt -lt 30 -and $null -eq $inputWindow; $attempt++) {
        Start-Sleep -Milliseconds 200
        $inputWindow = Find-QuickInputWindow -Root $Root -ProcessId $ProcessId -MainWindowHandle $MainWindowHandle
    }
    if ($null -eq $inputWindow) { throw 'OSC chatbox smoke could not open the quick-input window.' }

    return $inputWindow
}

function Invoke-OscChatboxSmoke {
    <#
    自身消息（手动输入）发到 VRChat 聊天框的那份文本必须可选带原文。这一份冒烟在隔离数据
    目录里跑真实发送链路（输入框 → 翻译 → OSC），用一个真实 UDP 监听当 VRChat 端，核对收到
    的 /chatbox/input 报文里有没有原文。隔离目录里只有内置的离线回显档案，所以译文就是输入
    本身：带原文＝这段文字出现两次（译文 + 原文），不带原文＝只出现一次。断言不依赖网络与密钥。
    #>
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Ensure-ValidationSmokeNative

    $smokeDataDirectory = Join-Path $env:TEMP ('vrc-osc-chatbox-smoke-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $smokeDataDirectory | Out-Null
    $listener = [System.Net.Sockets.UdpClient]::new(0)
    $port = ([System.Net.IPEndPoint]$listener.Client.LocalEndPoint).Port
    $probeText = '冒烟原文-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
    $settingsPath = Join-Path $smokeDataDirectory 'v2-user-settings.json'
    $process = $null
    try {
        # OSC 客户端在应用启动时按 settings 里的 host/port 建一次，所以端口必须在启动前写好；
        # 带不带原文是每次发送时读的，可以在两次发送之间改。
        [System.IO.File]::WriteAllText($settingsPath, (@{
            OscHost = '127.0.0.1'
            OscPort = $port
            IncludeOriginalInOsc = $true
        } | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
        $env:VRC_TRANSLATE_DATA_DIR = $smokeDataDirectory
        $env:VRC_TRANSLATE_SMOKE_PAGE = 'input'
        $process = Start-Process -FilePath $executable -WorkingDirectory $desktopOutput -PassThru
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $window = $null
        for ($attempt = 0; $attempt -lt 40 -and $null -eq $window; $attempt++) {
            Start-Sleep -Milliseconds 200
            $window = @($root.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.Condition]::TrueCondition)) |
                Where-Object { $_.Current.Name -eq 'VRCTranslate' -and $_.Current.ProcessId -eq $process.Id } |
                Select-Object -First 1
        }
        if ($null -eq $window) { throw 'OSC chatbox smoke could not find the shell window.' }
        # 与手工诊断一致的稳定点：等主窗口完全就绪再发（否则输入浮窗可能刚建好还没接收键盘）。
        Start-Sleep -Seconds 4

        foreach ($includeOriginal in @($true, $false)) {
            [System.IO.File]::WriteAllText($settingsPath, (@{
                OscHost = '127.0.0.1'
                OscPort = $port
                IncludeOriginalInOsc = $includeOriginal
            } | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))

            $inputWindow = Open-QuickInputWindow -Root $root -ProcessId $process.Id -MainWindowHandle $window.Current.NativeWindowHandle
            $textBox = Set-QuickInputText -Root $root -ProcessId $process.Id -Text $probeText
            Send-QuickInputText -TextBox $textBox -WindowHandle ([IntPtr]$inputWindow.Current.NativeWindowHandle)

            $payload = Receive-OscChatboxPacket -Client $listener -TimeoutMs 25000
            if ($null -eq $payload) {
                throw ('No /chatbox/input datagram arrived with IncludeOriginalInOsc set to ' + $includeOriginal + '.')
            }

            # 隔离数据目录里每个目标语言都用离线回显档案，所以「译文」这一份总是等于输入本身，
            # 出现几次就说明载荷里有几段译/原文；开与关只差末尾那一段原文，这个差值才是断言对象。
            $occurrences = ([regex]::Matches($payload, [regex]::Escape($probeText))).Count
            $expectedOccurrences = if ($includeOriginal) { 3 } else { 2 }
            if ($occurrences -ne $expectedOccurrences) {
                throw ("The chatbox payload must carry the original text exactly when the switch is on (expected " +
                    $expectedOccurrences + ' occurrences, found ' + $occurrences + "): '" + $payload + "'.")
            }
            if (-not $payload.EndsWith($probeText, [StringComparison]::Ordinal)) {
                throw ("The original text must be the last part of the chatbox payload: '" + $payload + "'.")
            }

            try { $inputWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
            Start-Sleep -Milliseconds 400
        }

        Write-Host '  OSC chatbox payload smoke passed (original on and off, real datagram).' -ForegroundColor Green
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        $listener.Dispose()
        Remove-Item Env:VRC_TRANSLATE_DATA_DIR -ErrorAction SilentlyContinue
        Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        Remove-Item -LiteralPath $smokeDataDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $startupLog) {
        throw 'OSC chatbox smoke wrote a startup failure log.'
    }
}

function Invoke-QuickInputUiSmoke {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Remove-Item $startupLog -ErrorAction SilentlyContinue
    $env:VRC_TRANSLATE_SMOKE_PAGE = 'input'
    $process = $null
    try {
        $process = Start-Process -FilePath $executable -WorkingDirectory $desktopOutput -PassThru
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $window = $null
        for ($attempt = 0; $attempt -lt 30 -and $null -eq $window; $attempt++) {
            Start-Sleep -Milliseconds 200
            $window = @($root.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.Condition]::TrueCondition)) |
                Where-Object { $_.Current.Name -eq 'VRCTranslate' -and $_.Current.ProcessId -eq $process.Id } |
                Select-Object -First 1
        }
        if ($null -eq $window) { throw 'Quick-input smoke could not find the VRCTranslate window.' }

        $openCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '打开输入框')
        $openButton = $null
        for ($attempt = 0; $attempt -lt 30 -and $null -eq $openButton; $attempt++) {
            Start-Sleep -Milliseconds 200
            $openButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $openCondition)
        }
        if ($null -eq $openButton) { throw 'Self-input page does not expose the quick-input entry.' }
        $openButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $inputWindow = $null
        for ($attempt = 0; $attempt -lt 30 -and $null -eq $inputWindow; $attempt++) {
            Start-Sleep -Milliseconds 200
            $inputWindow = @($root.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Window)))) |
                Where-Object {
                    $_.Current.ProcessId -eq $process.Id -and
                    $_.Current.NativeWindowHandle -ne $window.Current.NativeWindowHandle -and
                    $null -ne $_.FindFirst(
                        [System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                            'quick-input-text')))
                } | Select-Object -First 1
        }
        if ($null -eq $inputWindow) { throw 'Opening the self-input entry did not create the quick-input window.' }

        $inputRect = $inputWindow.Current.BoundingRectangle
        Assert-OverlayBounds -Bounds $inputRect -ConfiguredSize $quickOverlayDefaultSize -Label 'Quick-input'

        $controls = @($inputWindow.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition))
        $textBox = $controls | Where-Object { $_.Current.Name -eq '待翻译文字' -or $_.Current.AutomationId -eq 'quick-input-text' } | Select-Object -First 1
        if ($null -eq $textBox) { throw 'Quick-input window does not expose a named text box.' }
        $forbiddenInputControls = @('目标语言', '第二语言', '展开第二语言', '收起第二语言', '翻译并发送')
        $unexpectedInputControl = $controls | Where-Object { $_.Current.Name -in $forbiddenInputControls } | Select-Object -First 1
        if ($null -ne $unexpectedInputControl) {
            throw "Quick-input window exposes a duplicate or forbidden control: $($unexpectedInputControl.Current.Name)."
        }
        if ($controls | Where-Object { $_.Current.Name -eq '翻译并发送' }) { throw 'Quick-input window must not expose a send button.' }

        $textRect = $textBox.Current.BoundingRectangle
        if ($textRect.Width -le 0 -or $textRect.Height -le 0) {
            throw 'Quick-input controls have zero-sized layout bounds.'
        }
        $inputStyle = [VrcTranslateValidationNative]::GetWindowLongPtr(
            [IntPtr]$inputWindow.Current.NativeWindowHandle, -16).ToInt64()
        # Native captions and resize frames are intentional: Windows owns the
        # drag, cursor, snap, DPI and eight-direction resize behavior.
        if (($inputStyle -band 0x00C00000) -ne 0x00C00000) {
            throw 'The quick-input overlay lost its standard Windows caption.'
        }
        if (($inputStyle -band 0x00040000) -eq 0) {
            throw 'The quick-input overlay lost the standard Windows resize frame.'
        }

        try { $inputWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
        Write-Host '  Quick-input window interaction smoke passed.'
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    if (Test-Path $startupLog) {
        throw "Quick-input UI smoke wrote a startup failure log:`n$(Get-Content $startupLog -Raw)"
    }
}

function Invoke-SubtitleVisualSmoke {
    <#
    The caption surface used to be checked through its left activity mark. That
    mark is gone - it looked like a microphone and implied the app records the
    user - so this smoke checks what the surface really is now: one dark client
    area that is actually drawn, a message strip that starts at the surface edge
    instead of behind the deleted indicator column, and no leftover accent pixels
    or markup from that indicator. Real captions appear only after speech is
    recognized, which this smoke deliberately does not wait for; one deterministic
    probe caption is seeded instead (VRC_TRANSLATE_SMOKE_CAPTION), so the check of
    where the message list sits inside the surface can run (D7).
    #>
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Drawing
    if ($null -eq ('VrcTranslateSubtitleVisualNative' -as [type])) {
        $drawingAssembly = [System.Drawing.Bitmap].Assembly.Location
        $drawingReferences = @($drawingAssembly)
        $gdiPlusAssembly = Join-Path (Split-Path $drawingAssembly) 'System.Private.Windows.GdiPlus.dll'
        $windowsCoreAssembly = Join-Path (Split-Path $drawingAssembly) 'System.Private.Windows.Core.dll'
        if (Test-Path $gdiPlusAssembly) { $drawingReferences += $gdiPlusAssembly }
        if (Test-Path $windowsCoreAssembly) { $drawingReferences += $windowsCoreAssembly }
        Add-Type -ReferencedAssemblies $drawingReferences @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class VrcTranslateSubtitleVisualNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);

    public static RECT GetRectValue(IntPtr hwnd) {
        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) throw new InvalidOperationException("GetWindowRect failed.");
        return rect;
    }

    public static void Capture(IntPtr hwnd, string path) {
        RECT rect = GetRectValue(hwnd);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Invalid subtitle bounds.");
        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        using (var graphics = Graphics.FromImage(bitmap)) {
            var hdc = graphics.GetHdc();
            try {
                if (!PrintWindow(hwnd, hdc, 2)) throw new InvalidOperationException("PrintWindow failed.");
            }
            finally { graphics.ReleaseHdc(hdc); }
            bitmap.Save(path, ImageFormat.Png);
        }
    }
}
'@
    }

    Remove-Item $startupLog -ErrorAction SilentlyContinue
    $firstCapture = Join-Path $env:TEMP 'v2-validation-subtitle-visual-1.png'
    $secondCapture = Join-Path $env:TEMP 'v2-validation-subtitle-visual-2.png'
    Remove-Item $firstCapture, $secondCapture -Force -ErrorAction SilentlyContinue
    $env:VRC_TRANSLATE_SMOKE_PAGE = 'run'
    # D7：字幕只有在真实识别之后才出现，而这个冒烟不等语音；用一个确定性的字幕探针
    # 才能在浮窗里核对消息列表的位置（顶部对齐 vs 旧的贴底）。
    $env:VRC_TRANSLATE_SMOKE_CAPTION = '1'
    $process = $null
    $firstBitmap = $null
    $secondBitmap = $null
    try {
        $process = Start-Process -FilePath $executable -WorkingDirectory $desktopOutput -PassThru
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $mainWindow = $null
        for ($attempt = 0; $attempt -lt 40 -and $null -eq $mainWindow; $attempt++) {
            Start-Sleep -Milliseconds 200
            $mainWindow = @($root.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.Condition]::TrueCondition)) |
                Where-Object { $_.Current.Name -eq 'VRCTranslate' -and $_.Current.ProcessId -eq $process.Id } |
                Select-Object -First 1
        }
        if ($null -eq $mainWindow) { throw 'Subtitle visual smoke could not find the shell window.' }

        if ($null -eq ('VrcTranslateValidationNative' -as [type])) {
            Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VrcTranslateValidationNative {
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
}
'@
        }

        # D3：浮窗没有独立入口，它随他人语音识别一起出现。一次总开关＝开始识别 + 显示浮窗。
        $configuredVoiceHotkey = Get-ConfiguredGlobalHotkey 'VoiceHotkey' 'F7'
        Invoke-TestHotkey $configuredVoiceHotkey

        $subtitleWindow = $null
        $subtitleId = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'subtitle-text')
        for ($attempt = 0; $attempt -lt 60 -and $null -eq $subtitleWindow; $attempt++) {
            Start-Sleep -Milliseconds 200
            $windows = @($root.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Window))))
            $subtitleWindow = $windows |
                Where-Object {
                    $_.Current.ProcessId -eq $process.Id -and
                    $_.Current.NativeWindowHandle -ne $mainWindow.Current.NativeWindowHandle -and
                    [VrcTranslateValidationNative]::IsWindowVisible([IntPtr]$_.Current.NativeWindowHandle) -and
                    $null -ne $_.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $subtitleId)
                } | Select-Object -First 1
        }
        if ($null -eq $subtitleWindow) { throw 'The 他人语音 shortcut did not show the caption surface.' }

        $windowRect = [VrcTranslateSubtitleVisualNative]::GetRectValue([IntPtr]$subtitleWindow.Current.NativeWindowHandle)
        $windowWidth = $windowRect.Right - $windowRect.Left
        $windowHeight = $windowRect.Bottom - $windowRect.Top
        Assert-OverlayBounds -Bounds ([pscustomobject]@{ Width = $windowWidth; Height = $windowHeight }) `
            -ConfiguredSize $subtitleOverlayDefaultSize -Label 'Subtitle visual'

        # Border/Grid elements do not always publish UIA peers in WinUI 3, so the
        # stable text peer is measured. With the microphone-looking mark gone the
        # message strip must start at the surface edge instead of behind the 46px
        # indicator column it used to sit next to.
        $textCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'subtitle-text')
        $textElement = $subtitleWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
        if ($null -eq $textElement) { throw 'Subtitle visual smoke could not find the caption text peer.' }
        $textRect = $textElement.Current.BoundingRectangle
        if ([double]::IsInfinity($textRect.Width) -or [double]::IsInfinity($textRect.Height) -or
            $textRect.Width -lt 80 -or $textRect.Height -lt 20) {
            throw "Caption text has an unusable layout: $($textRect.Width)x$($textRect.Height)."
        }
        # 消息条必须在浮窗里水平居中：删除左侧标识列后左右留白应当对称（旧布局的
        # 34px 标识列 + 12px 间距会让左侧多出 46px）。
        $leftInset = [int][Math]::Round($textRect.Left - $windowRect.Left)
        $rightInset = [int][Math]::Round($windowRect.Right - $textRect.Right)
        if ($leftInset -gt 64 -or [Math]::Abs($leftInset - $rightInset) -gt 20) {
            throw "The caption message strip must be centred across the whole surface: left inset $leftInset, right inset $rightInset."
        }

        $markCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'subtitle-activity-mark')
        if ($null -ne $subtitleWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $markCondition)) {
            throw 'The removed activity mark is still exposed on the caption surface.'
        }

        # Capture the real window: the client area must actually be painted, and
        # no accent pixel of the deleted indicator ring may survive anywhere in
        # the caption region (the native title bar is excluded from the scan).
        # D7：这一份冒烟字幕是"仅译文"的白色文字，因此下面这套强调色启发式不需要改写。
        $handle = [IntPtr]$subtitleWindow.Current.NativeWindowHandle
        [VrcTranslateSubtitleVisualNative]::Capture($handle, $firstCapture)
        $firstBitmap = [System.Drawing.Bitmap]::new($firstCapture)
        $regionTop = [Math]::Max(0, [int][Math]::Floor($textRect.Top - $windowRect.Top) - 6)
        $surfacePixels = 0
        $accentPixels = 0
        for ($y = $regionTop; $y -lt $firstBitmap.Height; $y += 4) {
            for ($x = 0; $x -lt $firstBitmap.Width; $x += 4) {
                $pixel = $firstBitmap.GetPixel($x, $y)
                if ([Math]::Abs($pixel.R - 11) -le 14 -and [Math]::Abs($pixel.G - 23) -le 14 -and [Math]::Abs($pixel.B - 38) -le 14) {
                    $surfacePixels++
                }
                elseif ($pixel.G -gt 58 -and ($pixel.G - $pixel.R) -gt 14 -and ($pixel.B - $pixel.R) -gt 10) {
                    $accentPixels++
                }
            }
        }
        if ($surfacePixels -lt 200) {
            throw "The caption surface is not visibly rendered (dark surface pixels: $surfacePixels)."
        }
        if ($accentPixels -gt 0) {
            throw "The caption surface still renders activity-indicator accent pixels ($accentPixels)."
        }
        Write-Host "  Subtitle surface render smoke passed ($surfacePixels surface pixels, no activity mark)." -ForegroundColor Green

        # D7：把浮窗拉高到远高于内容，字幕必须留在客户区上半部分。旧的底部对齐会把这唯一
        # 一条消息压到客户区最下边，因此这条断言正是用户反馈的那个现象的反向验证。
        $tallHeight = 520
        if (-not [VrcTranslateSubtitleVisualNative]::MoveWindow($handle, $windowRect.Left, $windowRect.Top, $windowWidth, $tallHeight, $true)) {
            throw 'The caption surface could not be enlarged for the top-alignment check.'
        }
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            Start-Sleep -Milliseconds 100
            $enlargedRect = [VrcTranslateSubtitleVisualNative]::GetRectValue($handle)
            if (($enlargedRect.Bottom - $enlargedRect.Top) -ge ($tallHeight - 8)) { break }
        }
        Start-Sleep -Milliseconds 400
        $enlargedRect = [VrcTranslateSubtitleVisualNative]::GetRectValue($handle)
        $enlargedHeight = $enlargedRect.Bottom - $enlargedRect.Top
        if ($enlargedHeight -lt ($tallHeight - 8)) {
            throw "The caption surface did not accept the enlarged size: $enlargedHeight px."
        }
        $enlargedText = $subtitleWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
        if ($null -eq $enlargedText) { throw 'Subtitle visual smoke lost the caption text peer after enlarging the surface.' }
        $enlargedTextRect = $enlargedText.Current.BoundingRectangle
        $clientTop = [int][Math]::Round($enlargedTextRect.Top - $enlargedRect.Top)
        $clientHeight = [int][Math]::Round($enlargedTextRect.Height)
        if ($clientHeight -lt 200) { throw "The enlarged caption client area is unusable: $clientHeight px." }

        [VrcTranslateSubtitleVisualNative]::Capture($handle, $secondCapture)
        $secondBitmap = [System.Drawing.Bitmap]::new($secondCapture)
        $firstTextRow = -1
        $enlargedSurfacePixels = 0
        $enlargedAccentPixels = 0
        for ($y = $clientTop; $y -lt [Math]::Min($clientTop + $clientHeight, $secondBitmap.Height); $y += 4) {
            for ($x = 0; $x -lt $secondBitmap.Width; $x += 4) {
                $pixel = $secondBitmap.GetPixel($x, $y)
                if ([Math]::Abs($pixel.R - 11) -le 14 -and [Math]::Abs($pixel.G - 23) -le 14 -and [Math]::Abs($pixel.B - 38) -le 14) {
                    $enlargedSurfacePixels++
                }
                elseif ($pixel.G -gt 58 -and ($pixel.G - $pixel.R) -gt 14 -and ($pixel.B - $pixel.R) -gt 10) {
                    $enlargedAccentPixels++
                }
                elseif ($firstTextRow -lt 0 -and $pixel.R -gt 90 -and $pixel.G -gt 90 -and $pixel.B -gt 90) {
                    $firstTextRow = $y
                }
            }
        }
        if ($firstTextRow -lt 0) {
            throw 'The caption surface did not render the smoke caption.'
        }
        $textOffset = $firstTextRow - $clientTop
        if ($textOffset -gt [int]($clientHeight / 2)) {
            throw "The caption messages must start in the upper half of the surface: first text row at $textOffset of $clientHeight px."
        }
        # 放大后的表面扫描面积比收缩时更大：空出来的那一片同样不得出现强调色像素。
        if ($enlargedSurfacePixels -lt 200) {
            throw "The enlarged caption surface is not visibly rendered (dark surface pixels: $enlargedSurfacePixels)."
        }
        if ($enlargedAccentPixels -gt 0) {
            throw "The enlarged caption surface renders activity-indicator accent pixels ($enlargedAccentPixels)."
        }
        Write-Host "  Subtitle top-alignment smoke passed (first text row at $textOffset of $clientHeight px, $enlargedSurfacePixels surface pixels)." -ForegroundColor Green

        # 浮窗随识别停止而消失：再按一次总开关，识别和窗口必须一起关掉。
        Invoke-TestHotkey $configuredVoiceHotkey
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            Start-Sleep -Milliseconds 200
            if (-not [VrcTranslateValidationNative]::IsWindowVisible($handle)) { break }
        }
        if ([VrcTranslateValidationNative]::IsWindowVisible($handle)) {
            throw 'The 他人语音 shortcut did not hide the caption surface after this smoke.'
        }
    }
    finally {
        if ($null -ne $firstBitmap) { $firstBitmap.Dispose() }
        if ($null -ne $secondBitmap) { $secondBitmap.Dispose() }
        if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
        Remove-Item Env:VRC_TRANSLATE_SMOKE_CAPTION -ErrorAction SilentlyContinue
        Remove-Item $firstCapture, $secondCapture -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    if (Test-Path $startupLog) {
        throw "Subtitle visual smoke wrote a startup failure log:`n$(Get-Content $startupLog -Raw)"
    }
}

function Invoke-CompactLayoutSmoke {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    if ($null -eq ('VrcTranslateValidationNative' -as [type])) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VrcTranslateValidationNative {
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    public static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
'@
    }

    foreach ($page in @('run', 'settings', 'input', 'translation')) {
        Remove-Item $startupLog -ErrorAction SilentlyContinue
        $env:VRC_TRANSLATE_SMOKE_PAGE = $page
        $process = $null
        try {
            $process = Start-Process -FilePath $executable -WorkingDirectory $desktopOutput -PassThru
            $root = [System.Windows.Automation.AutomationElement]::RootElement
        $window = $null
        for ($attempt = 0; $attempt -lt 30 -and $null -eq $window; $attempt++) {
            Start-Sleep -Milliseconds 200
            $window = @($root.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.Condition]::TrueCondition)) |
                Where-Object { $_.Current.Name -eq 'VRCTranslate' -and $_.Current.ProcessId -eq $process.Id } |
                Select-Object -First 1
        }
            if ($null -eq $window) { throw "Compact '$page' smoke could not find the VRCTranslate window." }

            [VrcTranslateValidationNative]::SetWindowPos(
                [IntPtr]$window.Current.NativeWindowHandle,
                [IntPtr]::Zero, 80, 80, 960, 720, 0x0040) | Out-Null
            Start-Sleep -Milliseconds 700
            $windowRect = $window.Current.BoundingRectangle
            $ids = switch ($page) {
                'run' { @('route-summary-config') }
                'settings' { @('QuickInputHotkeyBox', 'VoiceHotkeyBox', 'SelfVoiceHotkeyBox') }
                # Border elements are not consistently exposed by WinUI's
                # UIA tree, so assert the interactive controls that prove the
                # narrow layout was actually measured and rendered.
                'input' { @('SelfVoiceStatusText', 'self-voice-start', 'self-voice-microphone') }
                'translation' { @('translation-profile-add') }
            }
            foreach ($automationId in $ids) {
                $condition = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
                $control = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
                if ($null -eq $control) { throw "Compact '$page' smoke could not find '$automationId'." }
                $rect = $control.Current.BoundingRectangle
                if ($rect.Width -le 40 -or $rect.Height -le 20 -or
                    [double]::IsInfinity($rect.Width) -or [double]::IsInfinity($rect.Height) -or
                    $rect.Left -lt $windowRect.Left - 2 -or $rect.Right -gt $windowRect.Right + 2) {
                    throw "Compact '$page' control '$automationId' has an invalid or clipped layout: $($rect.Width)x$($rect.Height)."
                }
            }
            if ($page -eq 'settings') {
                # D9：快捷键字段是"点击后按键录制"的捕获按钮，不再是可编辑 TextBox。
                # 这里只做非破坏性检查：控件类型正确、点击后进入录制态（不写盘）、
                # 焦点离开即取消且磁盘上的值保持原样。真正的落盘与轮询生效由
                # 人工/交互式 UIA 证据覆盖（见 docs 记录）。
                $userSettingsFile = Join-Path $desktopOutput 'data\v2-user-settings.json'
                $storedBefore = if (Test-Path $userSettingsFile) { (Get-Content -Raw $userSettingsFile | ConvertFrom-Json).QuickInputHotkey } else { 'Ctrl+Alt+I' }
                $hotkeyCondition = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'QuickInputHotkeyBox')
                $hotkeyBox = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $hotkeyCondition)
                if ($hotkeyBox.Current.ControlType -ne [System.Windows.Automation.ControlType]::Button) {
                    throw 'Shortcut fields must be capture buttons, not text boxes with a built-in delete button.'
                }
                $hotkeyBox.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 400
                $recordingCaps = @($hotkeyBox.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition)) |
                    Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text } |
                    ForEach-Object { $_.Current.Name }
                if (-not (@($recordingCaps) -join '|').Contains('请按下')) {
                    throw "Clicking a shortcut field did not start recording: '$(@($recordingCaps) -join '|')'."
                }
                $settingsNav = $window.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'nav-run')))
                if ($null -ne $settingsNav) { $settingsNav.SetFocus() }
                Start-Sleep -Milliseconds 400
                $storedAfter = if (Test-Path $userSettingsFile) { (Get-Content -Raw $userSettingsFile | ConvertFrom-Json).QuickInputHotkey } else { 'Ctrl+Alt+I' }
                if ($storedAfter -ne $storedBefore) {
                    throw "Leaving a recording shortcut field must not write anything: '$storedBefore' -> '$storedAfter'."
                }
                $reloadedCaps = @($hotkeyBox.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition)) |
                    Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text } |
                    ForEach-Object { $_.Current.Name }
                if ((@($reloadedCaps) -join '|').Contains('请按下')) {
                    throw 'Leaving a recording shortcut field did not cancel the recording.'
                }
            }
            if ($page -eq 'translation') {
                # Profile cards move their action buttons below the labels at
                # compact widths. Verify both the card actions and the editor
                # dialog remain visible inside the 960x720 shell.
                $profileEdit = $null
                $profileDeletes = @()
                for ($attempt = 0; $attempt -lt 30 -and $null -eq $profileEdit; $attempt++) {
                    Start-Sleep -Milliseconds 150
                    $elements = @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
                    $profileEdit = $elements | Where-Object {
                        $_.Current.AutomationId -match '^edit-.+' -and
                        $_.Current.BoundingRectangle.Width -gt 0 -and
                        $_.Current.BoundingRectangle.Height -gt 0 -and
                        -not [double]::IsInfinity($_.Current.BoundingRectangle.Width)
                    } | Select-Object -First 1
                    $profileDeletes = @($elements | Where-Object { $_.Current.AutomationId -match '^delete-.+' })
                }
                if ($null -eq $profileEdit) { throw "Compact '$page' smoke could not find a visible profile edit button." }
                $visibleDeletes = @($profileDeletes | Where-Object {
                    $candidateRect = $_.Current.BoundingRectangle
                    $candidateRect.Width -gt 0 -and $candidateRect.Height -gt 0 -and
                    -not [double]::IsInfinity($candidateRect.Width) -and
                    -not [double]::IsInfinity($candidateRect.Height)
                })
                if ($visibleDeletes.Count -eq 0) { throw "Compact '$page' smoke could not find a visible profile delete button." }
                foreach ($profileAction in @($profileEdit) + @($visibleDeletes)) {
                    $rect = $profileAction.Current.BoundingRectangle
                    if ($rect.Width -le 40 -or $rect.Height -le 20 -or
                        [double]::IsInfinity($rect.Width) -or [double]::IsInfinity($rect.Height) -or
                        $rect.Left -lt $windowRect.Left - 2 -or $rect.Right -gt $windowRect.Right + 2) {
                        throw "Compact '$page' profile action '$($profileAction.Current.AutomationId)' is clipped."
                    }
                }

                $profileEdit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                $dialogCondition = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, '编辑翻译服务档案')
                $profileDialog = $null
                for ($attempt = 0; $attempt -lt 30 -and $null -eq $profileDialog; $attempt++) {
                    Start-Sleep -Milliseconds 150
                    $profileDialog = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $dialogCondition)
                }
                if ($null -eq $profileDialog) { throw "Compact '$page' smoke could not open the profile editor dialog." }
                $dialogRect = $profileDialog.Current.BoundingRectangle
                if ($dialogRect.Width -le 300 -or $dialogRect.Height -le 240 -or
                    $dialogRect.Left -lt $windowRect.Left - 2 -or $dialogRect.Right -gt $windowRect.Right + 2) {
                    throw "Compact '$page' profile editor dialog is clipped: $($dialogRect.Width)x$($dialogRect.Height)."
                }
                $dialogCancel = $profileDialog.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::NameProperty, '取消')))
                if ($null -eq $dialogCancel) { throw "Compact '$page' profile editor dialog has no cancel action." }
                $dialogCancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 250

                # Scroll the outer page to the glossary and check that the two
                # editors and add action have finite, usable bounds.
                $scrollPane = $null
                $elements = @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
                foreach ($element in $elements) {
                    try {
                        $null = $element.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
                        if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Pane) { $scrollPane = $element; break }
                    } catch { }
                }
                if ($null -ne $scrollPane) {
                    $scrollPattern = $scrollPane.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
                    $scrollPattern.SetScrollPercent(-1, 100)
                    Start-Sleep -Milliseconds 350
                }
                foreach ($automationId in @('TermSourceBox', 'TermTargetBox', 'glossary-add')) {
                    $condition = New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
                    $control = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
                    if ($null -eq $control) { throw "Compact '$page' smoke could not find '$automationId' after scrolling to the glossary." }
                    $rect = $control.Current.BoundingRectangle
                    # Virtualized ListView content can report an infinite
                    # rectangle while it is outside the viewport. In that
                    # case the existence check above is still meaningful; if
                    # the control is realized, enforce its actual bounds.
                    if (-not [double]::IsInfinity($rect.Width) -and -not [double]::IsInfinity($rect.Height) -and
                        ($rect.Width -le 40 -or $rect.Height -le 20 -or
                         $rect.Left -lt $windowRect.Left - 2 -or $rect.Right -gt $windowRect.Right + 2)) {
                        throw "Compact '$page' glossary control '$automationId' is clipped."
                    }
                }
            }
            Write-Host "  '$page' compact 960x720 layout passed."
        }
        finally {
            if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
            Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
        if (Test-Path $startupLog) {
            throw "Compact '$page' smoke wrote a startup failure log:`n$(Get-Content $startupLog -Raw)"
        }
    }
}

Write-Host '[3/5] Running translation UI interaction smoke'
Invoke-TranslationUiSmoke

Write-Host '[4/5] Smoke testing all desktop pages'
Invoke-CompactLayoutSmoke
$overlayWindowSmoke = Join-Path $PSScriptRoot 'Invoke-V2OverlayWindowSmoke.ps1'
& powershell -NoProfile -ExecutionPolicy Bypass -File $overlayWindowSmoke -Executable $executable
if ($LASTEXITCODE -ne 0) { throw "Native overlay-window smoke failed with exit code $LASTEXITCODE." }
Invoke-QuickInputUiSmoke
# 自身消息的 OSC 载荷是否带原文：隔离数据目录里跑真实发送链路并用真实 UDP 报文核对。
Invoke-OscChatboxSmoke
Invoke-VoiceUiSmoke
Invoke-SubtitleVisualSmoke
foreach ($page in @('run', 'input', 'voice', 'translation', 'settings', 'guide')) {
    Invoke-DesktopSmoke $page
}

Write-Host '[5/5] Checking smoke-test artifacts'
if (Test-Path $startupLog) { throw "A stale startup error log remains: $startupLog" }
$remainingDesktopProcesses = @()
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    $remainingDesktopProcesses = @(Get-Process -Name VrcTranslate -ErrorAction SilentlyContinue | Where-Object { $_.HandleCount -gt 0 })
    if ($remainingDesktopProcesses.Count -eq 0) { break }
    Start-Sleep -Milliseconds 250
}
if ($remainingDesktopProcesses.Count -gt 0) {
    $ids = $remainingDesktopProcesses.Id -join ', '
    throw "Validation left VrcTranslate.exe process(es) running after all smoke tests: $ids."
}

Write-Host 'V2 validation passed.' -ForegroundColor Green
