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
$oldNuget = $env:NUGET_PACKAGES
$oldDotnetHome = $env:DOTNET_CLI_HOME
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
    $dotnet = Join-Path $project '.build\dotnet\dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnet)) {
        $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if (!$command) { throw 'Install the x64 .NET 8 SDK to build KeyHold.' }
        $dotnet = $command.Source
    }
    if (!(& $dotnet --list-sdks | Where-Object { $_ -match '^8\.' })) { throw 'Install the x64 .NET 8 SDK to build KeyHold.' }
    if (!$env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $project '.build\nuget' }
    if (!$env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $project '.build\dotnet-cli' }
    $cargoArgs = @('--offline', '--locked', '--manifest-path', 'rust\Cargo.toml', '--target', $target)
    & $cargo "+$toolchain" build @cargoArgs --release
    if ($LASTEXITCODE -ne 0) { throw ('Rust build failed. Ensure the toolchain is installed: rustup toolchain install ' + $toolchain + ' --profile minimal') }
    $dll = Join-Path $targetDir "$target\release\keyhold_worker.dll"

    if ($Test) {
        & $cargo "+$toolchain" test @cargoArgs
        if ($LASTEXITCODE -ne 0) { throw 'Rust tests failed.' }
        foreach ($suite in 'CoreTests', 'WorkerTests', 'UiTests') {
            $suiteArtifacts = Join-Path $project ".build\managed-tests\$suite"
            $suiteOutput = Join-Path $suiteArtifacts 'run'
            & $dotnet build tests\KeyHold.Tests.csproj -c Release --artifacts-path $suiteArtifacts -o $suiteOutput "-p:Suite=$suite"
            if ($LASTEXITCODE -ne 0) { throw "Test build failed: $suite" }
            & (Join-Path $suiteOutput "$suite.exe")
            if ($LASTEXITCODE -ne 0) { throw "Test failed: $suite" }
        }
    } else {
        if (!$OutputDirectory) { $OutputDirectory = Join-Path $project '.build\winui' }
        $out = [IO.Path]::GetFullPath($OutputDirectory)
        & $dotnet publish KeyHold.csproj -c Release -o $out
        if ($LASTEXITCODE -ne 0) { throw 'WinUI 3 publish failed.' }
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
    $env:NUGET_PACKAGES = $oldNuget
    $env:DOTNET_CLI_HOME = $oldDotnetHome
    Pop-Location
}
