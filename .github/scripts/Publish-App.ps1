param(
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release',
    [string] $Output = 'artifacts/publish',
    [string] $Version = '1.0.0',
    [string] $AssemblyVersion = '1.0.0.0'
)

$ErrorActionPreference = 'Stop'
dotnet publish src/Orayo/Orayo.csproj -c $Configuration -r $Runtime --self-contained false `
    -p:WindowsAppSDKSelfContained=false -p:PublishSingleFile=false -p:PublishReadyToRun=true `
    "-p:Version=$Version" "-p:PackageVersion=$Version" "-p:InformationalVersion=$Version" `
    "-p:AssemblyVersion=$AssemblyVersion" "-p:FileVersion=$AssemblyVersion" -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Publishing the application failed.' }

dotnet publish src/Orayo.Launcher/Orayo.Launcher.csproj -c $Configuration -r $Runtime `
    "-p:Version=$Version" "-p:AssemblyVersion=$AssemblyVersion" "-p:FileVersion=$AssemblyVersion" -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Publishing the native launcher failed.' }

foreach ($file in @('Orayo.exe', 'Orayo.App.exe', 'Orayo.App.runtimeconfig.json', 'Microsoft.WindowsAppRuntime.Bootstrap.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $Output $file))) { throw "Missing published file: $file" }
}
foreach ($file in @('coreclr.dll', 'hostfxr.dll', 'Microsoft.ui.xaml.dll', 'onnxruntime.dll', 'DirectML.dll')) {
    if (Test-Path -LiteralPath (Join-Path $Output $file)) { throw "Unexpected bundled runtime: $file" }
}

# Noninteractive startup probes work even before the shared dependencies are installed.
$probe = Start-Process -FilePath (Join-Path (Resolve-Path $Output).Path 'Orayo.exe') `
    -ArgumentList '--check-runtime' -Wait -PassThru -WindowStyle Hidden
if ($probe.ExitCode -notin @(0, 1, 2, 3)) { throw "Runtime probe failed: $($probe.ExitCode)" }
Write-Output "Native launcher runtime probe: $($probe.ExitCode) (1=.NET missing, 2=Windows App Runtime missing)."
