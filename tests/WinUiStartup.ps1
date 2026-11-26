param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\.build\winui'),
    [switch] $ExerciseNewProfile
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$executable = (Resolve-Path -LiteralPath (Join-Path $OutputDirectory 'KeyHold.exe')).Path
$app = Get-Process KeyHold -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $executable -and $_.MainWindowHandle -ne [IntPtr]::Zero } |
    Select-Object -First 1
if (!$app) { throw 'Open the published KeyHold configuration window first.' }

$window = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
$elements = $window.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
$byId = @{}
foreach ($element in $elements) {
    $properties = $element.Current
    if ($properties.AutomationId) { $byId[$properties.AutomationId] = $properties }
}

if (!$byId.ContainsKey('AppTitleBar')) { throw 'The WinUI TitleBar is missing.' }
if ($byId['RunState'].Name -notmatch '\u5DF2\u6682\u505C|Paused') { throw 'KeyHold is expected to be paused.' }
if ($byId['CursorPosition'].Name -notmatch 'X\s*=\s*-?\d+.*Y\s*=\s*-?\d+') { throw 'Cursor coordinates are missing.' }
for ($row = 1; $row -le 15; $row++) {
    for ($column = 1; $column -le 7; $column++) {
        if (!$byId.ContainsKey("Mapping${row}Column${column}")) { throw "Mapping row $row is incomplete." }
    }
}

$modulePaths = @($app.Modules | ForEach-Object FileName)
$directory = Split-Path -Parent $executable
foreach ($name in 'Microsoft.UI.Xaml.dll', 'keyhold_worker.dll') {
    if ((Join-Path $directory $name) -notin $modulePaths) { throw "Missing local runtime module: $name" }
}
Write-Output 'PASS: WinUI title bar, paused state, live coordinates, 15 mapping rows, and local WinUI/Rust DLLs.'

if ($ExerciseNewProfile) {
    if ($window.Current.Name.EndsWith(' *')) { throw 'Finish any current profile edits before this check.' }
    function Find-Control([string] $id) {
        $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
    }
    $name = Find-Control 'ProfileName'
    $name.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('WinUI profile check')
    $new = Find-Control 'NewProfile'
    $new.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $discard = $null
    for ($attempt = 0; $attempt -lt 30 -and !$discard; $attempt++) {
        Start-Sleep -Milliseconds 150
        $discard = Find-Control 'SecondaryButton'
    }
    if (!$discard) { throw 'The unsaved edit did not produce a discard prompt.' }
    $discard.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    for ($attempt = 0; $attempt -lt 30 -and $window.Current.Name -notmatch 'New profile|\u65B0\u914D\u7F6E'; $attempt++) {
        Start-Sleep -Milliseconds 150
    }
    if ($window.Current.Name -notmatch 'New profile|\u65B0\u914D\u7F6E' -or $window.Current.Name.EndsWith(' *')) {
        throw 'The new blank profile should start without an unsaved marker.'
    }
    Write-Output 'PASS: User edits prompt before discard and loading a new profile leaves it clean.'
}
