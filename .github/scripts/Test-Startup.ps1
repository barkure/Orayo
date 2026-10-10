param([string] $Directory = 'artifacts/publish')

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path

function Wait-Window([Diagnostics.Process] $Process) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $Process.Refresh()
        if ($Process.HasExited) { throw "Application exited before creating a window: $($Process.ExitCode)" }
        if ($Process.MainWindowHandle -ne 0) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The application did not create a window.'
}

$probe = Start-Process "$root/Orayo.exe" -ArgumentList '--check-runtime' -Wait -PassThru -WindowStyle Hidden
if ($probe.ExitCode -notin @(0, 1, 2, 3)) { throw 'Runtime detection failed.' }
if ($probe.ExitCode -ne 0) {
    $prompt = Start-Process "$root/Orayo.exe" -PassThru -WindowStyle Hidden
    try {
        Wait-Window $prompt
        if ($prompt.MainWindowTitle -ne 'Orayo') { throw "Unexpected dependency prompt: $($prompt.MainWindowTitle)" }
        if (-not $prompt.CloseMainWindow()) { throw 'Could not cancel the dependency prompt.' }
        if (-not $prompt.WaitForExit(5000) -or $prompt.ExitCode -ne 0) { throw 'Cancelling the dependency prompt failed.' }
        Write-Output 'Native dependency prompt opened and cancelled successfully.'
    } finally {
        if (-not $prompt.HasExited) { $prompt.Kill() }
        $prompt.Dispose()
    }
}

# Install prerequisites only inside the disposable GitHub runner, never on a user's machine.
if ($probe.ExitCode -band 1) { throw 'The Windows CI runner must provide .NET Desktop Runtime 10.0.12+.' }
if ($probe.ExitCode -band 2) {
    $installer = Join-Path (Resolve-Path artifacts).Path 'WindowsAppRuntimeInstall-x64.exe'
    Invoke-WebRequest 'https://aka.ms/windowsappsdk/2.0/2.0.1/windowsappruntimeinstall-x64.exe' -OutFile $installer
    $install = Start-Process $installer -ArgumentList '--quiet' -Wait -PassThru -WindowStyle Hidden
    if ($install.ExitCode -notin @(0, 3010)) { throw "Windows runtime installation failed: $($install.ExitCode)" }
}
$ready = Start-Process "$root/Orayo.exe" -ArgumentList '--check-runtime' -Wait -PassThru -WindowStyle Hidden
if ($ready.ExitCode -ne 0) { throw "Dependencies still unavailable: $($ready.ExitCode)" }

# Loading the actual UI also tests its bootstrapper and renamed assembly/XAML resources.
$ui = Start-Process "$root/Orayo.App.exe" -PassThru -WindowStyle Hidden
try {
    Wait-Window $ui
    Write-Output "WinUI startup created a window: $($ui.MainWindowTitle)"
} finally {
    if (-not $ui.HasExited) { $ui.Kill() }
    $ui.Dispose()
}
