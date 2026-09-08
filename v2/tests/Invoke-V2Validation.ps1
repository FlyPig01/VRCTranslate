$ErrorActionPreference = 'Stop'

$v2Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $v2Root 'VrcTranslate.sln'
$desktopOutput = Join-Path $v2Root 'src\VrcTranslate.Desktop\bin\x64\Debug\net10.0-windows10.0.19041.0'
$executable = Join-Path $desktopOutput 'VrcTranslate.exe'
$icon = Join-Path $desktopOutput 'app.ico'
$logo = Join-Path $desktopOutput 'logo-mark.png'
$startupLog = Join-Path $env:TEMP 'VrcTranslate-startup.log'
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
$overlayChromeSource = Get-Content -Raw (Join-Path $desktopSource 'Pages\OverlayWindowChrome.cs')
$runMarkup = Get-Content -Raw $runPage
$runSource = Get-Content -Raw $runPageCode
$inputMarkup = Get-Content -Raw $inputPage
$voiceMarkup = Get-Content -Raw $voicePage
$voiceSource = Get-Content -Raw $voicePageCode
$overlayMarkup = Get-Content -Raw $overlayPage
$overlayCodeSource = Get-Content -Raw (Join-Path $desktopSource 'Pages\SubtitleOverlayWindow.xaml.cs')
$overlayLayoutContract = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Core\Settings\OverlayWindowLayout.cs')
$overlayLayoutStore = Get-Content -Raw (Join-Path $v2Root 'src\VrcTranslate.Infrastructure\Configuration\OverlayWindowLayoutStore.cs')

function Get-ConfiguredOverlaySize {
    param(
        [Parameter(Mandatory)]
        [string] $Source,
        [Parameter(Mandatory)]
        [string] $Label
    )

    $match = [regex]::Match(
        $Source,
        '(?s)OverlayWindowChrome\.Configure\(.*?,\s*(?<width>\d+)\s*,\s*(?<height>\d+)\s*,')
    if (-not $match.Success) {
        throw "$Label overlay does not pass a concrete default size to the shared chrome."
    }

    $width = [int]$match.Groups['width'].Value
    $height = [int]$match.Groups['height'].Value
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
if ($translationCode -notmatch 'google-free|google-cloud|tencent|aliyun') { throw 'The translation profile catalog must retain the legacy service types.' }
if ($translationCode -match 'Header = "原文语言"|Header = "翻译为"|existing\.SourceLanguage|existing\.TargetLanguage') { throw 'Translation profile dialogs must not expose source or target language settings.' }
if ($appStateSource -notmatch 'openai-compatible".*, "gpt-4\.1-mini"' -or $appStateSource -notmatch 'DefaultModelForProvider' -or $appStateSource -notmatch 'new\("tencent".*"auto".*"zh-CN"' -or $translationCode -notmatch '腾讯云 SecretKey' -or $translationCode -notmatch '阿里云 AccessKey Secret') { throw 'Translation profile defaults and provider-specific credential labels must be explicit.' }
if ($translationMarkup -notmatch 'ListViewItem|HorizontalContentAlignment="Stretch"') { throw 'Glossary rows must stretch to the table width.' }
if ($translationCode -notmatch 'DefaultTerms' -or $translationCode -notmatch 'if \(_terms\.Count == 0\)') { throw 'The glossary must seed its default terms when no saved terms exist.' }
if ($translationCode -notmatch 'LayoutCard' -or $translationCode -notmatch 'grid\.ActualWidth' -or $translationCode -notmatch 'HorizontalScrollBarVisibility = ScrollBarVisibility\.Disabled') { throw 'Translation profile cards and dialogs must adapt to compact widths without horizontal clipping.' }
if (($translationCode -notmatch 'CreateProviderPlaceholders|IsConfiguredProfile' -and $appStateSource -notmatch 'CreateProviderPlaceholders|IsConfiguredProfile') -or $appStateSource -notmatch 'Only this offline profile' -or $appStateSource -notmatch 'IsConfiguredRoute') { throw 'Translation profiles must hide unconfigured provider placeholders and keep only the local test profile by default.' }
if ($settingsMarkup -notmatch 'Text="打开输入框"' -or $settingsMarkup -match 'Text="自身输入"') { throw 'Shortcut settings must name the quick-input action as 打开输入框.' }
if ($settingsMarkup -notmatch 'LostFocus="OnHotkeyBoxLostFocus"' -or $settingsMarkup -notmatch 'KeyDown="OnHotkeyBoxKeyDown"' -or $settingsSource -notmatch 'ConfirmHotkeyChangeAsync' -or $settingsSource -notmatch 'ContentDialog') { throw 'Shortcut edits must require an explicit confirmation before being persisted.' }

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
if ($inputMarkup -notmatch 'MaxWidth="280"' -or $inputMarkup -notmatch 'HorizontalAlignment="Stretch"' -or $inputCodeForLayout -notmatch 'Grid\.SetColumnSpan\(SelfVoiceTextHost, 2\)') { throw 'Self voice status must keep a short activity bar and allow the status label to span the free compact column.' }
if ($quickInputSource -notmatch 'TextBox' -or $quickInputSource -match 'ComboBox|目标语言|第二语言|quick-target-language|quick-secondary-language' -or $quickInputSource -notmatch 'Translate(Self)?Async' -or $quickInputSource -notmatch 'SendChatboxAsync' -or $quickInputSource -notmatch 'SetTranslationPreview') { throw 'The global quick-input overlay must accept Chinese text, read its saved language pair from the input page, translate it, send it through OSC, and update the preview without duplicate language controls.' }
if ($quickInputSource -match 'AcceptsReturn\s*=\s*true' -or $quickInputSource -notmatch 'e\.Key\s*==\s*Windows\.System\.VirtualKey\.Enter' -or $quickInputSource -match '翻译并发送|_send\b' -or $quickInputSource -notmatch 'TrimForChatbox' -or $quickInputSource -notmatch 'OverlayWindowChrome\.Configure') { throw 'The quick-input overlay must submit on Enter without a send button, respect the VRChat message length limit, and use shared draggable/resizable chrome.' }
if ($overlayChromeSource -notmatch 'OverlayInteractionController' -or
    $overlayChromeSource -notmatch 'ResolveResizeEdges' -or
    $overlayChromeSource -notmatch 'GetCursorPos' -or
    $overlayChromeSource -notmatch 'SetWindowPos' -or
    $overlayChromeSource -notmatch 'WmNcLButtonDown' -or
    $overlayChromeSource -notmatch 'WmNcCalcSize' -or
    $overlayChromeSource -notmatch 'WmNcHitTest' -or
    $overlayChromeSource -notmatch 'WmSetCursor' -or
    $overlayChromeSource -notmatch 'SetWindowLongPtr' -or
    $overlayChromeSource -notmatch 'CallWindowProc') {
    throw 'Game overlays must use a native borderless client area with standard Windows move/resize hit testing and cursors.'
}
if ($mainWindowSource -notmatch 'StartGlobalHotkeys|PollGlobalHotkeys|GetAsyncKeyState' -or $mainWindowSource -notmatch 'OverlayWindowHost|ToggleQuickInput') { throw 'The shell must expose a working global shortcut route to the quick-input overlay.' }
if ($mainWindowSource -notmatch 'Closed\s*\+=\s*OnWindowClosed' -or $mainWindowSource -notmatch 'OnWindowClosed' -or $mainWindowSource -notmatch 'OverlayWindowHost\.CloseAll') { throw 'Closing the main shell must stop global polling and close both overlays.' }
if ($overlayHostSource -notmatch 'public\s+static\s+void\s+CloseAll' -or $overlayHostSource -notmatch 'window\.Close\(\)') { throw 'OverlayWindowHost must close native overlay windows instead of only hiding them.' }
if ((Get-Content -Raw (Join-Path $desktopSource 'Pages\SelfMessagePage.xaml.cs')) -notmatch 'LocalSpeech|RecognizeSelfVoiceSamplesAsync|ToggleSelfVoiceFromHotkey|SendChatboxAsync') { throw 'Self-voice must use the local speech service and retain the OSC send path.' }
if ((Get-Content -Raw (Join-Path $desktopSource 'Pages\SelfMessagePage.xaml.cs')) -match 'SpeechRecognizer|Windows\.Media\.SpeechRecognition') { throw 'Self-voice must not fall back to Windows SpeechRecognizer.' }
if ($inputMarkup -match '发送格式|仅发送译文|原文 \+ 译文|仅发送原文') { throw 'Quick input must not expose an unused send-format selector.' }
$inputSource = Get-Content -Raw $inputPage.Replace('.xaml', '.xaml.cs')
if ($inputSource -notmatch 'v2-self-voice-settings\.json' -or $inputSource -notmatch 'ToggleSelfVoiceFromHotkey' -or $inputSource -notmatch 'State\.AudioCapture\.Create\(AudioCaptureMode\.Microphone' -or $inputSource -notmatch 'SamplesReady' -or $inputSource -notmatch 'Task\.WhenAny' -or $inputSource -match 'MediaCapture') { throw 'Self voice must retain persisted controls, a hotkey entry point, and a real microphone frame test.' }
if ($inputSource -notmatch 'LocalSpeechCaptureSession' -or $inputSource -notmatch 'AudioCaptureMode\.Microphone' -or $inputSource -notmatch 'session\.StartAsync' -or $inputSource -notmatch 'session\.DisposeAsync') { throw 'Self voice must connect the microphone capture session and release it on stop/unload.' }
if ($inputSource -match 'VrcTranslate\.Infrastructure') { throw 'Self voice page must use the application audio factory instead of a platform implementation.' }
if ($inputSource -notmatch 'TranslationPreviewChanged' -or $inputSource -notmatch 'RecentOriginal' -or $inputSource -notmatch 'RecentTranslation') { throw 'The main quick-input page must update its original/translated preview after a send.' }
if ($inputMarkup -match 'SelfVoiceLanguageBox|Header="识别语言"' -or $inputSource -match 'SelfVoiceLanguageBox') { throw 'Own voice input must remain Simplified Chinese and must not expose a recognition-language selector.' }
if ($inputMarkup -notmatch 'Text="翻译语言"' -or $inputMarkup -notmatch 'PrimaryTargetBox' -or $inputMarkup -notmatch 'SecondaryTargetBox' -or $inputMarkup -match 'TargetLanguageExpander' -or $inputSource -notmatch 'TranslateSelfAsync|LastSecondaryTranslatedText') { throw 'The input page must expose visible primary and optional second output-language settings with a dual preview.' }
if ($appStateSource -notmatch 'TranslationTargetSet|SelfTranslationTargets|TranslateSelfAsync|LastSecondaryTranslatedText') { throw 'AppState must persist and route the own-message target language pair.' }
if ($voiceMarkup -match '目标进程|刷新进程|显示字幕覆盖层|VRChat\.exe' -or $voiceMarkup -match '<ComboBox[^>]*(识别语言|目标语言)|Header="(识别语言|目标语言)"' -or $voiceMarkup -match 'ComboBoxItem Content="当前系统输出"') { throw 'VoicePage must keep process and subtitle-language controls out of the main page.' }
if ($voiceMarkup -match '当前翻译方案|默认翻译方案|识别后的文字会使用默认翻译服务') { throw 'VoicePage must not present translation-route details in the speech-recognition workflow.' }
if ($voiceMarkup -notmatch '本地语音模型' -or $voiceMarkup -notmatch 'Whisper Base' -or $voiceMarkup -notmatch '管理模型' -or $voiceMarkup -notmatch 'Content="字幕"') { throw 'VoicePage must expose local model management and one concise subtitle entry.' }
if ($voiceSource -notmatch 'State\.LocalSpeech|GetModelStatus|InstallModelAsync|RecognizeLocalSamplesAsync') { throw 'VoicePage must expose the local Whisper model state and use the application speech boundary.' }
if ($voiceSource -match 'SpeechRecognizer|Windows\.Media\.SpeechRecognition') { throw 'VoicePage must not use Windows SpeechRecognizer.' }
if ($voiceSource -notmatch 'LocalSpeechCaptureSession' -or $voiceSource -notmatch 'AudioCaptureMode\.SystemLoopback' -or $voiceSource -notmatch 'session\.StartAsync' -or $voiceSource -notmatch 'session\.DisposeAsync') { throw 'VoicePage must connect the system-loopback capture session and release it on stop/unload.' }
if ($voiceSource -match 'VrcTranslate\.Infrastructure') { throw 'Voice page must use the application audio factory instead of a platform implementation.' }
if ($voiceMarkup -match 'Windows 系统识别 · 无需密钥') { throw 'VoicePage must keep the recognition status label concise.' }
if ([regex]::Matches($voiceMarkup, 'Content="字幕"').Count -ne 1) { throw 'VoicePage must expose one concise subtitle entry instead of duplicate buttons.' }
if ($voiceMarkup -notmatch 'ProgressRing|EntranceThemeTransition' -or $voiceMarkup -notmatch 'VoiceAudioLevel' -or $voiceSource -notmatch 'VoicePulse\.IsActive' -or $voiceSource -notmatch 'LevelChanged|OnAudioLevelChanged') { throw 'VoicePage must provide a compact animated recognition state and measured audio activity.' }
if ($voiceSource -notmatch 'ContentDialog' -or $voiceSource -notmatch 'OverlayWindowHost|ShowSubtitle' -or $voiceSource -notmatch 'InstallModelAsync' -or $voiceSource -notmatch 'RemoveModelAsync') { throw 'VoicePage must provide a local model management dialog and expose a real subtitle preview window.' }
if ($voiceMarkup -match '1\s+音频来源|2\s+识别服务|3\s+字幕窗口') { throw 'VoicePage must not use numbered explanatory cards.' }
if ($overlayMarkup -match '识别语言|目标语言|显示(?:内容)?|他人语音字幕|等待') { throw 'Subtitle overlay must stay minimal: automatic recognition and fixed Simplified Chinese output have no visible labels or selectors.' }
if ($overlayMarkup -match 'SourceLanguageBox|TargetLanguageBox|DisplayModeBox|OverlayStatusText|OverlayStatusDot|Content="他人语音字幕"') { throw 'Subtitle overlay must not render redundant names, language selectors, display selectors, or status widgets.' }
if ($overlayMarkup -notmatch 'AutomationProperties.AutomationId="subtitle-text"' -or
     $overlayMarkup -notmatch 'x:Name="DragSurface"' -or
    $overlayCodeSource -notmatch 'SelectedTargetLanguage\s*=>\s*"zh-CN"' -or
    $overlayCodeSource -notmatch 'SelectedSourceLanguage\s*=>\s*"auto"' -or
    $overlayCodeSource -notmatch 'OverlayWindowChrome\.Configure') {
     throw 'Subtitle overlay must expose one compact surface, keep automatic recognition and Simplified Chinese output, and use shared draggable/resizable chrome.'
 }
 if ($overlayMarkup -match '(?i)waveform|wave-bar|wavebar|right-decoration|audio-bars' -or
     ([regex]::Matches($overlayMarkup, 'subtitle-activity-mark').Count -ne 1)) {
     throw 'Subtitle overlay must keep only the left activity mark; the removed irregular right-side decoration must not return.'
 }
if ($quickInputSource -notmatch 'OverlayWindowChrome\.Configure' -or
    $quickInputSource -notmatch 'initialLayout:\s*OverlayWindowHost\.GetSavedLayout' -or
    $quickInputSource -notmatch 'layoutChanged:\s*layout\s*=>\s*OverlayWindowHost\.SaveLayout' -or
    $overlayCodeSource -notmatch 'OverlayWindowChrome\.Configure' -or
    $overlayCodeSource -notmatch 'initialLayout:\s*OverlayWindowHost\.GetSavedLayout' -or
    $overlayCodeSource -notmatch 'layoutChanged:\s*layout\s*=>\s*OverlayWindowHost\.SaveLayout') {
    throw 'Both overlays must pass a concrete default size and their saved layout through the shared chrome.'
}
if ($overlayHostSource -notmatch 'v2-overlay-layout\.json' -or
    $overlayHostSource -notmatch 'GetSavedLayout' -or
    $overlayHostSource -notmatch 'SaveLayout' -or
    $overlayHostSource -notmatch '\.Flush\(\)' -or
    $overlayChromeSource -notmatch 'ResolveInitialLayout' -or
    $overlayChromeSource -notmatch 'PublishLayout' -or
    $overlayChromeSource -notmatch 'TryGetLayout' -or
    $overlayLayoutContract -notmatch 'record struct OverlayWindowLayout' -or
    $overlayLayoutStore -notmatch 'ScheduleSaveLocked' -or
    $overlayLayoutStore -notmatch 'File\.Move\(temporaryPath') {
     throw 'Overlay position and size persistence must use a layered layout contract, coalesced atomic storage, and native move/resize callbacks.'
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
    $quickInputSource -match 'Opacity\s*=\s*1\b' -or
    $quickInputSource -notmatch 'ColorHelper\.FromArgb\(a,' -or
    $quickInputSource -notmatch 'Content\s*=\s*_surface' -or
    $quickInputSource -match 'windowSurface' -or
    $quickInputSource -match 'private readonly Border _surface' -or
    $quickInputSource -match 'BorderBrush\s*=\s*Brush\("#B34FC9C2"\)' -or
    $quickInputSource -match 'CornerRadius\s*=\s*new CornerRadius' -or
    $quickInputSource -match 'Padding\s*=\s*new Thickness\(8, 7, 8, 7\)' -or
    $quickInputSource -notmatch 'private readonly Grid _surface' -or
    $quickInputSource -notmatch 'new Grid\s*\{' -or
    $quickInputSource -notmatch 'Background\s*=\s*Brush\("#00000000"\)' -or
     $quickInputSource -notmatch 'BorderThickness\s*=\s*new Thickness\(0\)') {
     throw 'Quick-input overlay must use one borderless translucent Grid clipped by the native rounded window region without an inner rounded frame.'
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
 if ($quickInputSource -notmatch 'UseSystemFocusVisuals\s*=\s*false' -or
     $quickInputSource -notmatch 'TextControlBorderThemeThicknessFocused' -or
     $quickInputSource -notmatch 'Padding\s*=\s*new Thickness\(0\)') {
     throw 'Quick-input focus visuals must remain transparent and borderless in every TextBox state.'
 }
if ($outputFormatterSource -notmatch 'Separator\s*=\s*" / "' -or
    $outputFormatterSource -notmatch 'AddIfPresent\(parts, primaryTranslation\)' -or
    $outputFormatterSource -notmatch 'AddIfPresent\(parts, secondaryTranslation\)' -or
    $outputFormatterSource -notmatch 'AddIfPresent\(parts, originalText\)' -or
    $outputFormatterSource -notmatch 'OscChatboxMaxUtf16Length\s*=\s*144') {
    throw 'Translation output formatting must use one slash-separated primary/secondary/original order and the VRChat 144 UTF-16 limit.'
}
if ($quickInputSource -notmatch 'TranslationOutputFormatter\.Format' -or
    $quickInputSource -notmatch 'result\.FormattedText' -or
    $selfMessageSource -notmatch 'TranslationOutputFormatter\.FormatForOsc') {
    throw 'Own-input preview and own-voice OSC output must share the Core translation formatter.'
}
if ($voiceSource -notmatch 'TranslationOutputFormatter\.TrimForOsc' -or
    $voiceSource -match 'TrimForChatbox\s*\(' -or
    $voiceSource -match 'CombinedText|FormattedText') {
    throw 'Subtitle OSC output must use the shared length guard while keeping its single Simplified Chinese translation.'
}
if ($overlayMarkup -notmatch '<Grid x:Name="DragSurface"' -or
     $overlayMarkup -match 'x:Name="DragSurface"[\s\S]{0,500}(BorderBrush|BorderThickness|CornerRadius|Padding)=' -or
     $overlayMarkup -notmatch 'Background="#[0-9A-Fa-f]{8}"' -or
     $overlayMarkup -notmatch 'subtitle-activity-mark' -or
     $overlayMarkup -notmatch 'x:Name="PulseRing"' -or
     $overlayCodeSource -notmatch 'ExtendsContentIntoTitleBar\s*=\s*true' -or
     $overlayCodeSource -notmatch 'OnVisualTimerTick' -or
     $overlayCodeSource -notmatch 'PulseRing\.Opacity') {
     throw 'Subtitle overlay must use one alpha-backed borderless Grid with a compact activity mark and a visible animation.'
 }
if ($overlayChromeSource -notmatch 'SetBorderAndTitleBar\(hasBorder: false, hasTitleBar: false\)' -or
    $overlayChromeSource -notmatch 'TryDisableNativeFrame' -or
    $overlayChromeSource -notmatch 'DwmNcRenderingPolicyDisabled\s*=\s*1' -or
    $overlayChromeSource -notmatch 'DwmaBorderColor\s*=\s*34' -or
    $overlayChromeSource -notmatch 'DwmColorNone' -or
    $overlayChromeSource -notmatch 'CreateRoundRectRgn' -or
    $overlayChromeSource -notmatch 'SetWindowRgn' -or
    $overlayChromeSource -notmatch 'ApplyRoundedWindowRegion') {
    throw 'Overlay chrome must remove the native caption and use one native rounded window region without a rectangular DWM frame.'
}
if ($overlayMarkup -match 'Shadow' -or $quickInputSource -match 'Shadow') { throw 'Game overlays must not add shadows over the game view.' }
if ($voiceSource -notmatch 'SpeechRecognitionRequest|State\.LocalSpeech' -or $voiceSource -notmatch '"zh-CN"') { throw 'VoicePage must use the local recognition boundary and keep translation output fixed to Simplified Chinese.' }
if ($voiceMarkup -notmatch 'voice-hotkey-summary|快捷键' -or $voiceSource -notmatch 'ReadGlobalVoiceHotkey') { throw 'VoicePage must display the configured subtitle shortcut.' }
if ($mainWindowSource -notmatch 'ToggleSubtitle') { throw 'The global subtitle shortcut must toggle the subtitle overlay visibility.' }
if ($hotkeyContractsSource -notmatch 'NormalizeModifier' -or $hotkeyContractsSource -notmatch 'IsSupportedPrimary' -or $hotkeyContractsSource -notmatch 'functionKey\s+is\s+>=\s+1\s+and\s+<=\s+12') { throw 'Core hotkey normalization must define the same supported modifier and primary-key set as the desktop poller.' }
if ($settingsValidationSource -notmatch 'HotkeyBinding\.Normalize\(gesture\)' -or $settingsValidationSource -notmatch 'gestures\[normalized\]') { throw 'Workspace settings validation must use canonical hotkey normalization for conflicts and unsupported keys.' }
if ($mainWindowSource -notmatch 'HotkeyBinding\.Normalize' -or $mainWindowSource -notmatch 'return HotkeyBinding\.Normalize\(fallback\)') { throw 'Desktop global hotkeys must use the core canonical normalization and safely fall back for invalid persisted values.' }
if ($mainWindowSource -notmatch 'OnTranslationPreviewChanged[\s\S]*DispatcherQueue\.HasThreadAccess' -or
    $mainWindowSource -notmatch 'OnTranslationPreviewChanged[\s\S]*DispatcherQueue\.TryEnqueue') {
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
if ($mainWindowSource -notmatch '"input"\s*=>\s*\(.*输入' -or $mainWindowSource -notmatch '"voice"\s*=>\s*\(.*字幕') { throw 'The desktop navigation must use the concise 输入 and 字幕 labels.' }
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

function Invoke-TranslationUiSmoke {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Remove-Item $startupLog -ErrorAction SilentlyContinue
    $env:VRC_TRANSLATE_SMOKE_PAGE = 'run'
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
        Write-Host '  Translation UI interaction smoke passed.'
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
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
    $settingsPath = Join-Path $env:LOCALAPPDATA 'VRCTranslate\v2-user-settings.json'
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

        $overlayButtonCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '打开字幕')
        $overlayButton = $null
        for ($attempt = 0; $attempt -lt 30 -and $null -eq $overlayButton; $attempt++) {
            Start-Sleep -Milliseconds 200
            $overlayButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $overlayButtonCondition)
        }
        if ($null -eq $overlayButton) { throw 'Voice page does not expose the subtitle window entry.' }
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
        $overlayButtonRect = $overlayButton.Current.BoundingRectangle
        $startButtonRect = $startButton.Current.BoundingRectangle
        if ([Math]::Abs($overlayButtonRect.Top - $startButtonRect.Top) -gt 24) {
            throw 'Voice page actions are vertically stacked at the normal window width.'
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
        if ($null -eq $hotkeySummary) { throw 'Voice page does not display the configured subtitle shortcut.' }
        $overlayButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $overlay = $null
        for ($attempt = 0; $attempt -lt 30 -and $null -eq $overlay; $attempt++) {
            Start-Sleep -Milliseconds 200
            $windows = @($root.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Window))))
            $overlay = $windows | Where-Object {
                $_.Current.ProcessId -eq $process.Id -and
                $_.Current.NativeWindowHandle -ne $window.Current.NativeWindowHandle -and
                    $null -ne $_.FindFirst(
                        [System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                            'subtitle-text')))
            } | Select-Object -First 1
        }
        if ($null -eq $overlay) { throw 'Opening the subtitle window did not create a visible window.' }
        $overlayControls = @($overlay.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition))
        if ($overlayControls | Where-Object { $_.Current.Name -in @('识别语言', '目标语言', '显示方式', '他人语音字幕', '等待') }) {
            throw 'The subtitle overlay still exposes redundant title, language, display, or status controls.'
        }
        $subtitleText = $overlayControls | Where-Object { $_.Current.AutomationId -eq 'subtitle-text' } | Select-Object -First 1
        # The frame is visible at startup so users can find and resize it; the
        # text itself must remain empty until the first recognized sentence.
        if ($null -ne $subtitleText -and -not [string]::IsNullOrEmpty($subtitleText.Current.Name)) {
            throw "The subtitle overlay must start empty; received '$($subtitleText.Current.Name)'."
        }
        $overlayRect = $overlay.Current.BoundingRectangle
        Assert-OverlayBounds -Bounds $overlayRect -ConfiguredSize $subtitleOverlayDefaultSize -Label 'Subtitle'
        $overlayStyle = [VrcTranslateValidationNative]::GetWindowLongPtr(
            [IntPtr]$overlay.Current.NativeWindowHandle, -16).ToInt64()
        if (($overlayStyle -band 0x00C00000) -ne 0) {
            throw 'The subtitle overlay still exposes a native caption.'
        }
        if (($overlayStyle -band 0x00040000) -eq 0) {
            throw 'The subtitle overlay lost the standard Windows resize frame.'
        }
        $overlayHandle = [IntPtr]$overlay.Current.NativeWindowHandle
        if (-not [VrcTranslateValidationNative]::IsWindowVisible($overlayHandle)) {
            throw 'The subtitle overlay must be visible before testing its global shortcut.'
        }
        # Read the same persisted shortcut the application reads. This keeps
        # the smoke test valid after a user changes the global binding.
        $configuredVoiceHotkey = Get-ConfiguredGlobalHotkey 'VoiceHotkey' 'F7'
        Invoke-TestHotkey $configuredVoiceHotkey
        Start-Sleep -Milliseconds 300
        if ([VrcTranslateValidationNative]::IsWindowVisible($overlayHandle)) {
            throw 'The subtitle shortcut did not hide the overlay.'
        }
        Invoke-TestHotkey $configuredVoiceHotkey
        Start-Sleep -Milliseconds 300
        if (-not [VrcTranslateValidationNative]::IsWindowVisible($overlayHandle)) {
            throw 'The subtitle shortcut did not show the overlay again.'
        }
        if ($overlayControls | Where-Object { $_.Current.Name -eq '关闭字幕窗口' -or $_.Current.Name -eq '关闭' }) {
            throw 'The subtitle overlay must not expose a close button.'
        }
        try { $overlay.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
        Write-Host '  Voice subtitle-window interaction smoke passed.'
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    if (Test-Path $startupLog) {
        throw "Voice UI smoke wrote a startup failure log:`n$(Get-Content $startupLog -Raw)"
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
        $forbiddenInputControls = @('目标语言', '第二语言', '展开第二语言', '收起第二语言', '翻译并发送', '关闭')
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
        # A standard resize loop needs WS_THICKFRAME. The caption and system
        # menu bits must stay removed because the XAML surface is the only
        # visible layer.
        if (($inputStyle -band 0x00C00000) -ne 0) {
            throw 'The quick-input overlay still exposes a native caption.'
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

function Assert-OverlayCapture([string] $page, [string] $target) {
    $capturePath = Join-Path $env:TEMP ("v2-validation-{0}.png" -f $target)
    Remove-Item $capturePath -ErrorAction SilentlyContinue
    $captureScript = Join-Path $PSScriptRoot 'Capture-ActualWindow.ps1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $captureScript `
        -Page $page -Target $target -OutputPath $capturePath | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $capturePath)) {
        throw "Could not capture the $target overlay for the visual smoke test."
    }

    Add-Type -AssemblyName System.Drawing
    $bitmap = $null
    try {
        $bitmap = [System.Drawing.Bitmap]::new($capturePath)
        $topRows = [Math]::Min(8, $bitmap.Height)
        $whitePixels = 0
        $sampleCount = 0
        for ($y = 0; $y -lt $topRows; $y++) {
            if ($y -eq 0) { continue }
            for ($x = 0; $x -lt $bitmap.Width; $x++) {
                # PrintWindow leaves the compositor's intentionally
                # transparent rounded corners as white bitmap pixels. Ignore
                # only that small corner envelope; a white strip across the
                # middle of an edge still fails the check below.
                if (($x -lt 14 -or $x -ge ($bitmap.Width - 14)) -and $y -lt 14) { continue }
                $pixel = $bitmap.GetPixel($x, $y)
                $sampleCount++
                if ($pixel.R -gt 200 -and $pixel.G -gt 200 -and $pixel.B -gt 200) {
                    $whitePixels++
                }
            }
        }
        $edgeBand = [Math]::Min(6, [Math]::Max(1, [Math]::Min($bitmap.Width, $bitmap.Height) / 4))
        $edgeWhitePixels = 0
        $edgeSampleCount = 0
        for ($y = 0; $y -lt $bitmap.Height; $y++) {
            for ($x = 0; $x -lt $bitmap.Width; $x++) {
                if ($y -eq 0) { continue }
                if ($x -lt $edgeBand -or $x -ge ($bitmap.Width - $edgeBand) -or
                    $y -lt $edgeBand -or $y -ge ($bitmap.Height - $edgeBand)) {
                    if (($x -lt 14 -or $x -ge ($bitmap.Width - 14)) -and
                        ($y -lt 14 -or $y -ge ($bitmap.Height - 14))) { continue }
                    $pixel = $bitmap.GetPixel($x, $y)
                    $edgeSampleCount++
                    if ($pixel.R -gt 200 -and $pixel.G -gt 200 -and $pixel.B -gt 200) {
                        $edgeWhitePixels++
                    }
                }
            }
        }
        if (($sampleCount -gt 0 -and ($whitePixels / [double]$sampleCount) -gt 0.05) -or
            ($edgeSampleCount -gt 0 -and ($edgeWhitePixels / [double]$edgeSampleCount) -gt 0.005)) {
            throw "$target overlay has white frame pixels: top $whitePixels/$sampleCount, edge $edgeWhitePixels/$edgeSampleCount."
        }
    }
    finally {
        if ($null -ne $bitmap) { $bitmap.Dispose() }
        Remove-Item $capturePath -ErrorAction SilentlyContinue
    }
    Write-Host "  '$target' overlay has no white top strip." -ForegroundColor Green
}

function Invoke-SubtitleVisualSmoke {
    <#
    The subtitle surface used to regress to a plain block even when the XAML
    still contained the decoration markup. Exercise the real window so the
    smoke test checks the rendered microphone ring animation, not only source
    text. The assertion follows the current activity-mark layout.
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

        $openCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '打开字幕')
        $openButton = $mainWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $openCondition)
        if ($null -eq $openButton) { throw 'Subtitle visual smoke could not find the 打开字幕 action.' }
        $openButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $subtitleWindow = $null
        $subtitleId = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'subtitle-text')
        for ($attempt = 0; $attempt -lt 40 -and $null -eq $subtitleWindow; $attempt++) {
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
                    $null -ne $_.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $subtitleId)
                } | Select-Object -First 1
        }
        if ($null -eq $subtitleWindow) { throw 'Subtitle visual smoke could not find the subtitle overlay.' }

        $windowRect = [VrcTranslateSubtitleVisualNative]::GetRectValue([IntPtr]$subtitleWindow.Current.NativeWindowHandle)
        $windowWidth = $windowRect.Right - $windowRect.Left
        $windowHeight = $windowRect.Bottom - $windowRect.Top
        Assert-OverlayBounds -Bounds ([pscustomobject]@{ Width = $windowWidth; Height = $windowHeight }) `
            -ConfiguredSize $subtitleOverlayDefaultSize -Label 'Subtitle visual'

        # Border/Grid elements do not always publish UIA peers in WinUI 3.
        # Use the stable text peer when available, then inspect the activity
        # mark zone on the left. This keeps the assertion about rendered pixels
        # independent of UIA virtualization and the exact window dimensions.
        $textCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'subtitle-text')
        $textElement = $subtitleWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
        if ($null -eq $textElement) { throw 'Subtitle visual smoke could not find the subtitle text peer.' }
        $textRect = $textElement.Current.BoundingRectangle
        if ([double]::IsInfinity($textRect.Width) -or [double]::IsInfinity($textRect.Height) -or
            $textRect.Width -lt 80 -or $textRect.Height -lt 20) {
            throw "Subtitle text has an unusable layout: $($textRect.Width)x$($textRect.Height)."
        }

        $handle = [IntPtr]$subtitleWindow.Current.NativeWindowHandle
        [VrcTranslateSubtitleVisualNative]::Capture($handle, $firstCapture)
        Start-Sleep -Milliseconds 240
        [VrcTranslateSubtitleVisualNative]::Capture($handle, $secondCapture)
        $firstBitmap = [System.Drawing.Bitmap]::new($firstCapture)
        $secondBitmap = [System.Drawing.Bitmap]::new($secondCapture)
        $activityCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'subtitle-activity-mark')
        $activityElement = $subtitleWindow.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $activityCondition)
        if ($null -ne $activityElement) {
            $activityRect = $activityElement.Current.BoundingRectangle
            $localLeft = [Math]::Max(0, [int][Math]::Floor($activityRect.Left - $windowRect.Left) - 10)
            $localTop = [Math]::Max(0, [int][Math]::Floor($activityRect.Top - $windowRect.Top) - 10)
            $localRight = [Math]::Min($firstBitmap.Width, [int][Math]::Ceiling($activityRect.Right - $windowRect.Left) + 10)
            $localBottom = [Math]::Min($firstBitmap.Height, [int][Math]::Ceiling($activityRect.Bottom - $windowRect.Top) + 10)
        }
        else {
            # Fallback for WinUI builds that omit Grid peers from UIA.
            $localLeft = 0
            $localTop = [Math]::Max(0, [int][Math]::Floor(($firstBitmap.Height - 64) / 2))
            $localRight = [Math]::Min($firstBitmap.Width, 84)
            $localBottom = [Math]::Min($firstBitmap.Height, $localTop + 64)
        }
        $activityPixels = 0
        $animatedPixels = 0
        for ($y = $localTop; $y -lt $localBottom; $y++) {
            for ($x = $localLeft; $x -lt $localRight; $x++) {
                $before = $firstBitmap.GetPixel($x, $y)
                $after = $secondBitmap.GetPixel($x, $y)
                if ($before.G -gt 58 -and ($before.G - $before.R) -gt 14 -and ($before.B - $before.R) -gt 10) {
                    $activityPixels++
                }
                if ([Math]::Abs($before.R - $after.R) + [Math]::Abs($before.G - $after.G) + [Math]::Abs($before.B - $after.B) -gt 8) {
                    $animatedPixels++
                }
            }
        }
        if ($activityPixels -lt 6) {
            throw "Subtitle activity mark is not visibly rendered (accent pixels: $activityPixels)."
        }
        if ($animatedPixels -lt 2) {
            throw "Subtitle activity mark did not animate between captures (changed pixels: $animatedPixels)."
        }
        Write-Host "  Subtitle activity mark render and animation smoke passed ($activityPixels accent, $animatedPixels changed pixels)." -ForegroundColor Green
    }
    finally {
        if ($null -ne $firstBitmap) { $firstBitmap.Dispose() }
        if ($null -ne $secondBitmap) { $secondBitmap.Dispose() }
        if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
        Remove-Item $firstCapture, $secondCapture -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    if (Test-Path $startupLog) {
        throw "Subtitle visual smoke wrote a startup failure log:`n$(Get-Content $startupLog -Raw)"
    }
}

function Invoke-OverlayResizeSmoke {
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
        if ($null -eq $window) { throw 'Overlay resize smoke could not find the shell window.' }

        $children = @($root.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Window))))
        $children = @($children | Where-Object { $_.Current.ProcessId -eq $process.Id })
        $inputWindow = $children | Where-Object {
            $null -ne $_.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                    'quick-input-text')))
        } | Select-Object -First 1
        if ($null -eq $inputWindow) { throw 'Overlay resize smoke could not find the input overlay.' }

        $subtitleWindow = $children | Where-Object {
            $null -ne $_.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                     'subtitle-text')))
        } | Select-Object -First 1
        if ($null -eq $subtitleWindow) { throw 'Overlay resize smoke could not find the subtitle overlay.' }

        $handle = [IntPtr]$inputWindow.Current.NativeWindowHandle
        if (-not [VrcTranslateValidationNative]::HasRoundedWindowRegion($handle)) {
            throw 'Input overlay is not clipped to a rounded native window region; a rectangular host frame could leak through.'
        }
        $inputBoundsForHitTest = [VrcTranslateValidationNative]::GetWindowRectValue($handle)
        $inputCornerHit = [VrcTranslateValidationNative]::HitTestAt(
            $handle,
            $inputBoundsForHitTest.Right - 3,
            $inputBoundsForHitTest.Bottom - 3)
        if ($inputCornerHit -ne 17) {
            throw "Input overlay bottom-right hit test returned $inputCornerHit instead of HTBOTTOMRIGHT (17)."
        }
        $inputTopHit = [VrcTranslateValidationNative]::HitTestAt(
            $handle,
            $inputBoundsForHitTest.Left + 3,
            $inputBoundsForHitTest.Top + 3)
        if ($inputTopHit -ne 13) {
            throw "Input overlay top-left hit test returned $inputTopHit instead of HTTOPLEFT (13)."
        }
        $inputCenterHit = [VrcTranslateValidationNative]::HitTestAt(
            $handle,
            [int](($inputBoundsForHitTest.Left + $inputBoundsForHitTest.Right) / 2),
            [int](($inputBoundsForHitTest.Top + $inputBoundsForHitTest.Bottom) / 2))
        if ($inputCenterHit -notin @(1, 2)) {
            throw "Input overlay center hit test returned unexpected code $inputCenterHit."
        }

        [VrcTranslateValidationNative]::ShowWindow($handle, 5) | Out-Null
        [VrcTranslateValidationNative]::SetForegroundWindow($handle) | Out-Null
        Start-Sleep -Milliseconds 200
        $before = [VrcTranslateValidationNative]::GetWindowRectValue($handle)
        $inputTextCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            'quick-input-text')
        $inputTextElement = $inputWindow.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $inputTextCondition)
        if ($null -eq $inputTextElement) { throw 'Overlay resize smoke could not find the input text area.' }
        $textBefore = $inputTextElement.Current.BoundingRectangle
        # Stay inside the rounded card while remaining within the ten-pixel
        # resize grip; its extreme corner is intentionally transparent.
        [VrcTranslateValidationNative]::SetCursorPos($before.Right - 8, $before.Bottom - 8) | Out-Null
        Start-Sleep -Milliseconds 100
        [VrcTranslateValidationNative]::MouseEvent(
            [VrcTranslateValidationNative]::MouseLeftDown, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 100
        [VrcTranslateValidationNative]::SetCursorPos($before.Right + 48, $before.Bottom + 28) | Out-Null
        Start-Sleep -Milliseconds 250
        [VrcTranslateValidationNative]::MouseEvent(
            [VrcTranslateValidationNative]::MouseLeftUp, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 250
        $after = [VrcTranslateValidationNative]::GetWindowRectValue($handle)
        if (($after.Right - $after.Left) -le (($before.Right - $before.Left) + 20) -or
            ($after.Bottom - $after.Top) -le (($before.Bottom - $before.Top) + 10)) {
            throw "Dragging the input overlay corner did not resize it: before $($before.Right - $before.Left)x$($before.Bottom - $before.Top), after $($after.Right - $after.Left)x$($after.Bottom - $after.Top)."
        }
        Start-Sleep -Milliseconds 350
        $textAfter = $inputTextElement.Current.BoundingRectangle
        if ($textAfter.Width -le ($textBefore.Width + 20) -or
            $textAfter.Height -le ($textBefore.Height + 8)) {
            throw "Input text area did not follow the native resize: before $([math]::Round($textBefore.Width))x$([math]::Round($textBefore.Height)), after $([math]::Round($textAfter.Width))x$([math]::Round($textAfter.Height))."
        }

        [VrcTranslateValidationNative]::SetWindowPos(
            $handle, [IntPtr]::Zero, $before.Left, $before.Top,
            $before.Right - $before.Left, $before.Bottom - $before.Top, 0x0040) | Out-Null

        # Exercise the same native edge/corner path on the subtitle overlay.
        # The two windows share the chrome implementation, but this catches
        # regressions where only the input overlay receives the resize hook.
        $subtitleHandle = [IntPtr]$subtitleWindow.Current.NativeWindowHandle
        if (-not [VrcTranslateValidationNative]::HasRoundedWindowRegion($subtitleHandle)) {
            throw 'Subtitle overlay is not clipped to a rounded native window region; a rectangular host frame could leak through.'
        }
        [VrcTranslateValidationNative]::ShowWindow($subtitleHandle, 5) | Out-Null
        [VrcTranslateValidationNative]::SetForegroundWindow($subtitleHandle) | Out-Null
        Start-Sleep -Milliseconds 200
        $subtitleBefore = [VrcTranslateValidationNative]::GetWindowRectValue($subtitleHandle)
        $subtitleCornerHit = [VrcTranslateValidationNative]::HitTestAt(
            $subtitleHandle,
            $subtitleBefore.Right - 3,
            $subtitleBefore.Bottom - 3)
        if ($subtitleCornerHit -ne 17) {
            throw "Subtitle overlay bottom-right hit test returned $subtitleCornerHit instead of HTBOTTOMRIGHT (17)."
        }
        [VrcTranslateValidationNative]::SetCursorPos($subtitleBefore.Right - 8, $subtitleBefore.Bottom - 8) | Out-Null
        Start-Sleep -Milliseconds 100
        [VrcTranslateValidationNative]::MouseEvent(
            [VrcTranslateValidationNative]::MouseLeftDown, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 100
        [VrcTranslateValidationNative]::SetCursorPos($subtitleBefore.Right + 48, $subtitleBefore.Bottom + 28) | Out-Null
        Start-Sleep -Milliseconds 250
        [VrcTranslateValidationNative]::MouseEvent(
            [VrcTranslateValidationNative]::MouseLeftUp, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 250
        $subtitleAfter = [VrcTranslateValidationNative]::GetWindowRectValue($subtitleHandle)
        if (($subtitleAfter.Right - $subtitleAfter.Left) -le (($subtitleBefore.Right - $subtitleBefore.Left) + 20) -or
            ($subtitleAfter.Bottom - $subtitleAfter.Top) -le (($subtitleBefore.Bottom - $subtitleBefore.Top) + 10)) {
            throw "Dragging the subtitle overlay corner did not resize it: before $($subtitleBefore.Right - $subtitleBefore.Left)x$($subtitleBefore.Bottom - $subtitleBefore.Top), after $($subtitleAfter.Right - $subtitleAfter.Left)x$($subtitleAfter.Bottom - $subtitleAfter.Top)."
        }

        [VrcTranslateValidationNative]::SetWindowPos(
            $subtitleHandle, [IntPtr]::Zero, $subtitleBefore.Left, $subtitleBefore.Top,
            $subtitleBefore.Right - $subtitleBefore.Left, $subtitleBefore.Bottom - $subtitleBefore.Top, 0x0040) | Out-Null

        # Exercise the same center-caption path used to move a normal Windows
        # window. Use the middle of the subtitle surface so no icon or edge
        # control can intercept the gesture, then restore the saved position.
        Start-Sleep -Milliseconds 150
        $dragBefore = [VrcTranslateValidationNative]::GetWindowRectValue($subtitleHandle)
        $dragX = [int](($dragBefore.Left + $dragBefore.Right) / 2)
        $dragY = [int](($dragBefore.Top + $dragBefore.Bottom) / 2)
        [VrcTranslateValidationNative]::SetCursorPos($dragX, $dragY) | Out-Null
        [VrcTranslateValidationNative]::MouseEvent(
            [VrcTranslateValidationNative]::MouseLeftDown, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 120
        [VrcTranslateValidationNative]::SetCursorPos($dragX + 42, $dragY + 22) | Out-Null
        Start-Sleep -Milliseconds 220
        [VrcTranslateValidationNative]::MouseEvent(
            [VrcTranslateValidationNative]::MouseLeftUp, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 250
        $dragAfter = [VrcTranslateValidationNative]::GetWindowRectValue($subtitleHandle)
        if ([math]::Abs(($dragAfter.Left - $dragBefore.Left)) -lt 20 -or
            [math]::Abs(($dragAfter.Top - $dragBefore.Top)) -lt 10) {
            throw "Dragging the subtitle surface did not move it: before ($($dragBefore.Left),$($dragBefore.Top)), after ($($dragAfter.Left),$($dragAfter.Top))."
        }
        [VrcTranslateValidationNative]::SetWindowPos(
            $subtitleHandle, [IntPtr]::Zero, $dragBefore.Left, $dragBefore.Top,
            $dragBefore.Right - $dragBefore.Left, $dragBefore.Bottom - $dragBefore.Top, 0x0040) | Out-Null
        Write-Host '  Input and subtitle overlay corner resize interactions passed.' -ForegroundColor Green
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        Remove-Item Env:VRC_TRANSLATE_SMOKE_PAGE -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    if (Test-Path $startupLog) {
        throw "Overlay resize smoke wrote a startup failure log:`n$(Get-Content $startupLog -Raw)"
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
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    public static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    public static extern bool PtInRegion(IntPtr hRgn, int x, int y);
    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    public static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    public const uint MouseLeftDown = 0x0002;
    public const uint MouseLeftUp = 0x0004;
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    public static RECT GetWindowRectValue(IntPtr hWnd) { RECT rect; GetWindowRect(hWnd, out rect); return rect; }
    public static bool HasWindowRegion(IntPtr hWnd) {
        IntPtr region = CreateRectRgn(0, 0, 1, 1);
        if (region == IntPtr.Zero) return false;
        try { return GetWindowRgn(hWnd, region) != 0; }
        finally { DeleteObject(region); }
    }
    public static bool HasRoundedWindowRegion(IntPtr hWnd) {
        IntPtr region = CreateRectRgn(0, 0, 1, 1);
        if (region == IntPtr.Zero) return false;
        try {
            if (GetWindowRgn(hWnd, region) == 0) return false;
            RECT rect;
            if (!GetWindowRect(hWnd, out rect)) return false;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 24 || height < 24) return false;
            var cornersOutside =
                !PtInRegion(region, 1, 1) &&
                !PtInRegion(region, width - 2, 1) &&
                !PtInRegion(region, 1, height - 2) &&
                !PtInRegion(region, width - 2, height - 2);
            var centerInside = PtInRegion(region, width / 2, height / 2);
            var edgeCentersInside =
                PtInRegion(region, width / 2, 1) &&
                PtInRegion(region, 1, height / 2) &&
                PtInRegion(region, width - 2, height / 2) &&
                PtInRegion(region, width / 2, height - 2);
            return cornersOutside && centerInside && edgeCentersInside;
        }
        finally { DeleteObject(region); }
    }
    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    public static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    public static int HitTestAt(IntPtr hWnd, int x, int y) {
        // WM_NCHITTEST carries the screen point in lParam.  Encoding the
        // coordinates directly avoids depending on SetCursorPos being
        // reflected before a synchronous SendMessage (which can otherwise
        // reuse the previous probe point and make the test flaky).
        SetCursorPos(x, y);
        var packed = (long)(short)x | ((long)(short)y << 16);
        return SendMessage(hWnd, 0x0084, IntPtr.Zero, (IntPtr)packed).ToInt32();
    }
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
                # A shortcut edit must remain in the editor until the user
                # explicitly confirms it. Use UIA to exercise the same path a
                # manual edit takes, then cancel so the smoke run is isolated.
                $hotkeyCondition = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'QuickInputHotkeyBox')
                $hotkeyBox = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $hotkeyCondition)
                $valuePattern = $hotkeyBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                $originalHotkey = $valuePattern.Current.Value
                $valuePattern.SetValue('Ctrl+Alt+J')
                $settingsNav = $window.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'nav-run')))
                if ($null -ne $settingsNav) { $settingsNav.SetFocus() }
                $confirmDialog = $null
                for ($attempt = 0; $attempt -lt 30 -and $null -eq $confirmDialog; $attempt++) {
                    Start-Sleep -Milliseconds 100
                    $confirmDialog = $window.FindFirst(
                        [System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::NameProperty, '确认快捷键')))
                }
                if ($null -eq $confirmDialog) { throw 'Changing a shortcut did not open the confirmation dialog.' }
                $cancelShortcut = $confirmDialog.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::NameProperty, '取消')))
                if ($null -eq $cancelShortcut) { throw 'Shortcut confirmation dialog has no cancel action.' }
                $cancelShortcut.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 200
                Start-Sleep -Milliseconds 200
                $reloadedHotkeyBox = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $hotkeyCondition)
                $reloadedValue = $reloadedHotkeyBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
                if ($reloadedValue -ne $originalHotkey) { throw 'Cancelling a shortcut edit did not restore the saved value.' }
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
Invoke-OverlayResizeSmoke
$layoutPersistenceSmoke = Join-Path $PSScriptRoot 'Invoke-V2OverlayLayoutPersistence.ps1'
& powershell -NoProfile -ExecutionPolicy Bypass -File $layoutPersistenceSmoke -Executable $executable
if ($LASTEXITCODE -ne 0) { throw "Overlay layout persistence smoke failed with exit code $LASTEXITCODE." }
Invoke-QuickInputUiSmoke
Invoke-VoiceUiSmoke
Assert-OverlayCapture 'input' 'quick-input'
Assert-OverlayCapture 'voice' 'subtitle'
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
