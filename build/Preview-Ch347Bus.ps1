param(
    [string]$OutputDirectory = '',
    [string]$PreviewDirectory = '',
    [switch]$VerifyProtocolTabs,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or -not [Environment]::Is64BitProcess -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use x64 Windows PowerShell 5.1 -STA.' }
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo "llcom plus\bin\x64\$Configuration" }
if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'llcom plus.exe') -PathType Leaf)) { throw 'Build the x64 application first or specify OutputDirectory.' }
if (-not $PreviewDirectory) { $PreviewDirectory = Join-Path $repo 'artifacts\ch347-ui' }
[void](New-Item -ItemType Directory -Force -Path $PreviewDirectory)
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$profileName = 'llcom-ch347-preview-' + [Guid]::NewGuid().ToString('N')
$profile = Join-Path $tempBase $profileName
[void](New-Item -ItemType Directory -Path $profile)
$app = $null; $globalType = $null; $page = $null
$rendered = 0
$dpiScales = if($VerifyProtocolTabs){@(1.0,1.25,1.5,2.0)}else{@(1.0)}
$fontSizes = if($VerifyProtocolTabs){@(12.0,14.0,18.0)}else{@(12.0)}
try {
    Add-Type -AssemblyName PresentationFramework,System.Xaml
    if($VerifyProtocolTabs){
        Add-Type -Path (Join-Path $PSScriptRoot 'Ch347TabLayoutRegressionProbe.cs') -ReferencedAssemblies @('System.dll',[Windows.DependencyObject].Assembly.Location,[Windows.Media.Visual].Assembly.Location,[Windows.FrameworkElement].Assembly.Location,[Windows.Markup.IQueryAmbient].Assembly.Location)
    }
    Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.dll' | ForEach-Object {
        try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
    }
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $OutputDirectory 'llcom plus.exe'))
    $globalType = $assembly.GetType('llcom_plus.Tools.Global',$true)
    $globalType.GetField('ProfilePath').SetValue($null,$profile + '\')
    $settings = [Activator]::CreateInstance($assembly.GetType('llcom_plus.Model.Settings',$true),$true)
    $globalType.GetField('setting').SetValue($null,$settings)
    [Windows.Application]::ResourceAssembly = $assembly
    $app = [Activator]::CreateInstance($assembly.GetType('llcom_plus.App',$true))
    $app.InitializeComponent()
    $instance = [Reflection.BindingFlags]'NonPublic,Public,Instance'
    [Windows.Application].GetField('_startupUri',$instance).SetValue($app,$null)
    $app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
    foreach ($language in @('zh-CN','en-US')) {
        $settings.language = $language
        [void]$globalType.GetMethod('LoadLanguageFile').Invoke($null,@($language))
        foreach ($dark in @($false,$true)) {
            [void]$globalType.GetMethod('ApplyTheme').Invoke($null,@($dark))
            $service = [Activator]::CreateInstance($assembly.GetType('llcom_plus.Tools.Ch347BusService',$true))
            $store = [Activator]::CreateInstance($assembly.GetType('llcom_plus.Tools.Ch347PresetStore',$true),$instance,$null,[object[]]@([string](Join-Path $profile 'presets.json')),$null)
            # No native enumeration: the page is constructed with refreshOnLoaded=false,
            # and rendered offscreen without a Window or a PresentationSource.
            $page = [Activator]::CreateInstance($assembly.GetType('llcom_plus.Pages.Ch347BusPage',$true),$instance,$null,[object[]]@($service,$store,$false),$null)
            $build = $page.GetType().GetMethod('BuildRequest',$instance)
            $i2c = $build.Invoke($page,$null)
            if ($i2c.I2cAddress -ne 0x50 -or $i2c.ReadLength -ne 1 -or $i2c.WriteData.Length -ne 0) { throw 'Invalid default I2C UI transaction.' }
            $content = $page.Content; $page.Content = $null
            $content.Resources.MergedDictionaries.Add($page.Resources)
            foreach ($scenario in @('i2c','spi','jtag-reset','jtag-ir','jtag-dr')) {
                $protocol = if($scenario -eq 'i2c'){0}elseif($scenario -eq 'spi'){1}else{2}
                $page.FindName('ProtocolTabs').SelectedIndex = $protocol
                if ($protocol -eq 1) {
                    $page.FindName('SpiOperationComboBox').SelectedIndex = 2
                    $page.FindName('SpiWriteTextBox').Text = '9F'
                    $page.FindName('SpiReadLengthTextBox').Text = '3'
                    $page.FindName('SpiDummyTextBox').Text = '00'
                    $spi = $build.Invoke($page,$null)
                    if ($spi.SpiDummyByte -ne 0 -or $spi.WriteData[0] -ne 0x9f -or $spi.ReadLength -ne 3) { throw 'SPI UI did not retain transaction parameters.' }
                }
                if ($protocol -eq 2) {
                    $jtagOperation = if($scenario -eq 'jtag-reset'){0}elseif($scenario -eq 'jtag-ir'){1}else{2}
                    $page.FindName('JtagOperationComboBox').SelectedIndex = $jtagOperation
                    $page.FindName('JtagBitLengthTextBox').Text = '9'
                    $page.FindName('JtagWriteTextBox').Text = 'A5 01'
                    $jtag = $build.Invoke($page,$null)
                    if ([int]$jtag.Protocol -ne 2 -or [int]$jtag.JtagOperation -ne $jtagOperation -or $jtag.JtagClockCode -ne 0) { throw 'JTAG UI lost its selected protocol/operation/clock.' }
                    if ($jtagOperation -eq 0) {
                        if ($jtag.JtagBitLength -ne 0 -or $jtag.WriteData.Length -ne 0 -or $jtag.ReadLength -ne 0 -or $page.FindName('JtagWriteTextBox').IsEnabled) { throw 'TAP reset must ignore disabled shift data and must not transfer a payload.' }
                    } elseif ($jtag.JtagBitLength -ne 9 -or $jtag.ReadLength -ne 2 -or $jtag.WriteData[0] -ne 0xa5 -or $jtag.WriteData[1] -ne 1 -or -not $page.FindName('JtagWriteTextBox').IsEnabled) { throw 'JTAG UI did not preserve nine-bit packed HEX input.' }
                    if ($page.FindName('PeriodicCheckBox').Visibility -ne [Windows.Visibility]::Collapsed -or $page.FindName('PeriodicCheckBox').IsChecked) { throw 'JTAG must never enable periodic execution.' }
                }
                foreach ($width in @(480,700)) {
                    foreach($fontSize in $fontSizes){
                    foreach($dpiScale in $dpiScales){
                    $surface = New-Object Windows.Controls.Border
                    $surface.Padding = New-Object Windows.Thickness(10)
                    $surface.Background = $app.TryFindResource('AppWindowBackgroundBrush')
                    $surface.SetValue([Windows.Documents.TextElement]::FontFamilyProperty,(New-Object Windows.Media.FontFamily('Microsoft YaHei UI')))
                    $surface.SetValue([Windows.Documents.TextElement]::FontSizeProperty,[double]$fontSize)
                    [Windows.Media.VisualTreeHelper]::SetRootDpi($surface,(New-Object Windows.DpiScale($dpiScale,$dpiScale)))
                    $surface.Child = $content
                    $surface.Measure((New-Object Windows.Size($width,850)))
                    $surface.Arrange((New-Object Windows.Rect(0,0,$width,850)))
                    $surface.UpdateLayout()
                    if($VerifyProtocolTabs){[void][Ch347TabLayoutRegressionProbe]::Run($page,$dpiScale)}
                    $pixelWidth=[int][Math]::Ceiling($width*$dpiScale)
                    $pixelHeight=[int][Math]::Ceiling(850*$dpiScale)
                    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($pixelWidth,$pixelHeight,(96*$dpiScale),(96*$dpiScale),[Windows.Media.PixelFormats]::Pbgra32)
                    $bitmap.Render($surface)
                    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
                    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
                    $theme = if($dark){'dark'}else{'light'}
                    $bus = $scenario
                    $variant = if($VerifyProtocolTabs){'-f'+$fontSize+'-dpi'+[int]($dpiScale*100)}else{''}
                    $path = Join-Path $PreviewDirectory "$bus-$language-$theme-$width$variant.png"
                    $stream = [IO.File]::Create($path)
                    try { $encoder.Save($stream) } finally { $stream.Dispose() }
                    $surface.Child = $null
                    $rendered++
                    if(-not $VerifyProtocolTabs){Write-Host "PASS Offscreen $bus $language $theme $width px: $path"}
                    }
                    }
                }
            }
            $page.Dispose(); $page=$null
        }
    }
    Write-Host "PASS $rendered offscreen renders; no hardware was enumerated or opened."
}
finally {
    if($null -ne $page){$page.Dispose()}
    if($null -ne $globalType){
        $globalType.GetProperty('isMainWindowsClosed').SetValue($null,$true,$null)
        [void]$assembly.GetType('llcom_plus.Tools.QuickSendBackupService',$true).GetMethod('Shutdown',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,$null)
    }
    if($null -ne $app){$app.Shutdown()}
    $resolved=[IO.Path]::GetFullPath($profile).TrimEnd('\')
    if([IO.Path]::GetDirectoryName($resolved) -ne $tempBase -or [IO.Path]::GetFileName($resolved) -ne $profileName -or $profileName -notmatch '^llcom-ch347-preview-[0-9a-f]{32}$'){throw 'Unsafe preview cleanup path.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
