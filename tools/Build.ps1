param(
    [switch] $Test,
    [string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$target = 'x86_64-pc-windows-gnu'
$toolchain = 'stable-' + $target
$targetDir = Join-Path $project '.build\rust-target'
$rustRoot = Join-Path $project '.build\rust-toolchain'
$localCargo = Join-Path $rustRoot 'cargo\bin\cargo.exe'
$oldCargo = $env:CARGO_HOME
$oldRustup = $env:RUSTUP_HOME
$oldTarget = $env:CARGO_TARGET_DIR
Push-Location -LiteralPath $project
try {
    if (Test-Path -LiteralPath $localCargo) {
        $cargo = $localCargo
        $env:CARGO_HOME = Join-Path $rustRoot 'cargo'
        $env:RUSTUP_HOME = Join-Path $rustRoot 'rustup'
    } else {
        $command = Get-Command cargo.exe -ErrorAction SilentlyContinue
        if (!$command) { throw 'Rust is required to build KeyHold. Install rustup and run: rustup toolchain install stable-x86_64-pc-windows-gnu --profile minimal' }
        $cargo = $command.Source
    }
    $env:CARGO_TARGET_DIR = $targetDir
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework compiler not found.' }

    $common = @('/nologo', '/utf8output', '/codepage:65001', '/warnaserror+')
    $data = @('/reference:System.Web.Extensions.dll', 'src\Feedback.cs', 'src\Localization.cs', 'src\Core.cs')
    $ui = @('/win32manifest:app.manifest', '/win32icon:assets\KeyHold.ico', '/resource:assets\KeyHold.ico,KeyHold.AppIcon',
        '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', 'src\FluentUi.cs', 'src\Native.cs',
        'src\KeyboardWorker.cs', 'src\MacroEditor.cs', 'src\Program.cs')
    function Compile([string[]] $arguments) {
        & $compiler @common @arguments
        if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
    }
    function Run-Test([string] $path) {
        & $path
        if ($LASTEXITCODE -ne 0) { throw ('Test failed: ' + $path) }
    }
    $cargoArgs = @('--offline', '--locked', '--manifest-path', 'rust\Cargo.toml', '--target', $target)
    & $cargo "+$toolchain" build @cargoArgs --release
    if ($LASTEXITCODE -ne 0) { throw ('Rust build failed. Ensure the toolchain is installed: rustup toolchain install ' + $toolchain + ' --profile minimal') }
    $dll = Join-Path $targetDir "$target\release\keyhold_worker.dll"

    if ($Test) {
        & $cargo "+$toolchain" test @cargoArgs
        if ($LASTEXITCODE -ne 0) { throw 'Rust tests failed.' }
        Compile (@('/target:exe', '/out:tests\CoreTests.exe') + $data + @('tests\LegacyEngine.cs', 'tests\FeedbackTests.cs', 'tests\SequenceTests.cs', 'tests\CoreTests.cs'))
        Run-Test '.\tests\CoreTests.exe'
        Compile (@('/target:exe', '/out:tests\WorkerTests.exe') + $data + @('tests\LegacyEngine.cs', 'tests\LegacyKeyboardWorker.cs', 'tests\WorkerTests.cs'))
        Run-Test '.\tests\WorkerTests.exe'
        Copy-Item -LiteralPath $dll -Destination 'tests' -Force
        Compile (@('/target:exe', '/platform:x64', '/main:UiTests', '/out:tests\UiTests.exe') + $data + $ui + @('tests\UiTests.cs'))
        Run-Test '.\tests\UiTests.exe'
    } else {
        if (!$OutputDirectory) { $OutputDirectory = $project }
        $out = [IO.Path]::GetFullPath($OutputDirectory)
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        Compile (@('/target:winexe', '/platform:x64', '/optimize+', ('/out:' + (Join-Path $out 'KeyHold.exe'))) + $data + $ui)
        Copy-Item -LiteralPath $dll -Destination $out -Force
        if ((Join-Path $out 'KeyHold.exe.config') -ne (Join-Path $project 'KeyHold.exe.config')) {
            Copy-Item -LiteralPath (Join-Path $project 'KeyHold.exe.config') -Destination $out -Force
        }
        Write-Output ('Build succeeded: ' + (Join-Path $out 'KeyHold.exe'))
    }
} finally {
    $env:CARGO_HOME = $oldCargo
    $env:RUSTUP_HOME = $oldRustup
    $env:CARGO_TARGET_DIR = $oldTarget
    Pop-Location
}
