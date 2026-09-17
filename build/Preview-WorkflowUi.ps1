param([ValidateSet('Debug','Release')][string]$Configuration='Debug')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$outputDir=Join-Path $root "llcom plus\bin\x64\$Configuration"
$previewDir=Join-Path $root 'artifacts\workflow-ui'
[void](New-Item -ItemType Directory -Force -Path $previewDir)
$profileDir=Join-Path $previewDir ('profile-'+[Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $profileDir)
Add-Type -AssemblyName PresentationFramework
Get-ChildItem -LiteralPath $outputDir -Filter '*.dll' | ForEach-Object {
    try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
}
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $outputDir 'llcom plus.exe'))
$globalType=$assembly.GetType('llcom_plus.Tools.Global',$true)
$settingsType=$assembly.GetType('llcom_plus.Model.Settings',$true)
$globalType.GetField('ProfilePath').SetValue($null,$profileDir+'\')
$settings=[Activator]::CreateInstance($settingsType,$true)
$globalType.GetField('setting').SetValue($null,$settings)
$flags=[Reflection.BindingFlags]'NonPublic,Instance'
[Windows.Application]::ResourceAssembly=$assembly
$app=[Activator]::CreateInstance($assembly.GetType('llcom_plus.App',$true))
$app.InitializeComponent()
[Windows.Application].GetField('_startupUri',$flags).SetValue($app,$null)
$app.ShutdownMode=[Windows.ShutdownMode]::OnExplicitShutdown
$exitCode=0
try {
    foreach ($theme in @('Light','Dark')) {
        $settingsType.GetField('_darkMode',$flags).SetValue($settings,($theme -eq 'Dark'))
        [void]$globalType.GetMethod('ApplyTheme').Invoke($null,[object[]]@(($theme -eq 'Dark')))
        foreach ($pageName in @('LogAnalysisPage','PlotPage','CircularSendPage','WorkspacePage')) {
            $type=$assembly.GetType("llcom_plus.Pages.$pageName",$true)
            $page=[Activator]::CreateInstance($type)
            # Invoke only these read-only tool initializers; never create/show the
            # application's normal window or load any serial connection page.
            if ($pageName -ne 'LogAnalysisPage') {
                $load=$type.GetMethod('Page_Loaded',$flags)
                if ($null -ne $load) { [void]$load.Invoke($page,[object[]]@($page,$null)) }
            }
            $content=$page.Content
            $page.Content=$null
            $content.DataContext=$page.DataContext
            $content.Resources.MergedDictionaries.Add($page.Resources)
            foreach ($width in @(470,800)) {
                $height=720
                $surface=New-Object Windows.Controls.Border
                $surface.Background=$app.TryFindResource('AppWindowBackgroundBrush')
                $surface.Child=$content
                $surface.Measure((New-Object Windows.Size($width,$height)))
                $surface.Arrange((New-Object Windows.Rect(0,0,$width,$height)))
                $surface.UpdateLayout()
                $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
                $bitmap.Render($surface)
                $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder
                $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
                $path=Join-Path $previewDir "$pageName-$theme-$width.png"
                $stream=[IO.File]::Open($path,[IO.FileMode]::Create)
                try { $encoder.Save($stream) } finally { $stream.Dispose() }
                $surface.Child=$null
                Write-Host "RENDER $path"
            }
            $unload=$type.GetMethod('Page_Unloaded',$flags)
            if ($null -ne $unload) { [void]$unload.Invoke($page,[object[]]@($page,$null)) }
            $close=$type.GetMethod('Global_ProgramClosed',$flags)
            if ($null -ne $close) { [void]$close.Invoke($page,[object[]]@($null,[EventArgs]::Empty)) }
        }
    }
    Write-Host 'PASS Workflow views render offscreen in both themes at narrow and wide sizes.'
}
catch { $exitCode=1; Write-Host $_.Exception.ToString() }
finally {
    $globalType.GetProperty('isMainWindowsClosed').SetValue($null,$true,$null)
    $app.Shutdown()
}
exit $exitCode
