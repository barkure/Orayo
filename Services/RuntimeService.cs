using System;
using System.Threading;
using System.Threading.Tasks;
using Orayo;
using Orayo.Models;

namespace Orayo.Services;

public sealed class RuntimeService
{
    private readonly XrayService _localXray = new();
    private readonly TunService _tunService = new();
    private readonly TunHelperClient _tunHelper = new();
    private ServerEntry? _activeServer;
    private bool _isRunning;
    private bool _isTunSession;
    private bool _isTransitioning;
    private bool _isShuttingDown;
    private CancellationTokenSource? _tunMonitorCts;
    private Task? _tunMonitorTask;

    public RuntimeService()
    {
        _localXray.RunningChanged += OnLocalXrayRunningChanged;
    }

    public bool IsRunning => _isRunning;

    public ServerEntry? ActiveServer => _activeServer;

    public string LastError => _localXray.LastError;

    public string TunHelperLastError => _tunHelper.LastError;

    public event EventHandler? StateChanged;

    public Task<bool> IsTunHelperAvailableAsync() => _tunHelper.IsAvailableAsync();

    public Task<bool> EnsureTunHelperAvailableAsync() => _tunHelper.EnsureAvailableAsync();

    public async Task<RuntimeConnectResult> ConnectAsync(ServerEntry server, AppSettings settings, AppRuntimeState runtimeState)
    {
        _isTransitioning = true;
        try
        {
            if (settings.IsTunMode)
            {
                await StopLocalSessionIfNeededAsync();
                await StopTunSessionIfNeededAsync();
                SystemProxyService.ClearProxy();
                UpdateState(isRunning: false, activeServer: null, isTunSession: false);

                var portConflict = await PortConflictService.EnsurePortsAvailableForCurrentXrayAsync(settings.LocalSocksPort, settings.LocalHttpPort);
                if (!string.IsNullOrWhiteSpace(portConflict))
                {
                    return RuntimeConnectResult.Failed(Strings.ErrConnectionFailed, portConflict);
                }

                if (!_tunService.IsWintunAvailable())
                {
                    return RuntimeConnectResult.Failed(Strings.ErrTunModeError, string.Format(Strings.ErrWintunNotFound, _tunService.GetExpectedWintunPath()));
                }

                if (!await _tunHelper.IsAvailableAsync())
                {
                    return RuntimeConnectResult.Failed(
                        Strings.ErrTunModeError,
                        string.IsNullOrWhiteSpace(_tunHelper.LastError) ? Strings.ErrCannotStartTunHelper : _tunHelper.LastError);
                }

                var config = XrayConfigBuilder.Build(server, settings);
                var response = await _tunHelper.StartAsync(config);
                if (response?.Success != true)
                {
                    SystemProxyService.ClearProxy();
                    return RuntimeConnectResult.Failed(
                        response?.ErrorTitle ?? Strings.ErrTunModeError,
                        response?.ErrorMessage
                            ?? (string.IsNullOrWhiteSpace(_tunHelper.LastError) ? Strings.ErrTunStartFailed : _tunHelper.LastError));
                }

                SystemProxyService.ClearProxy();
                runtimeState.LastSelectedServerId = server.Id;
                UpdateState(isRunning: true, activeServer: server, isTunSession: true);
                await StartTunMonitorAsync();
                return RuntimeConnectResult.Succeeded();
            }

            await StopTunSessionIfNeededAsync();
            await StopLocalSessionIfNeededAsync();
            SystemProxyService.ClearProxy();
            UpdateState(isRunning: false, activeServer: null, isTunSession: false);

            var localPortConflict = await PortConflictService.EnsurePortsAvailableForCurrentXrayAsync(settings.LocalSocksPort, settings.LocalHttpPort);
            if (!string.IsNullOrWhiteSpace(localPortConflict))
            {
                return RuntimeConnectResult.Failed(Strings.ErrConnectionFailed, localPortConflict);
            }

            var localConfig = XrayConfigBuilder.Build(server, settings);
            var localOk = await _localXray.StartAsync(localConfig);
            if (!localOk)
            {
                SystemProxyService.ClearProxy();
                UpdateState(isRunning: false, activeServer: null, isTunSession: false);
                return RuntimeConnectResult.Failed(
                    Strings.ErrConnectionFailed,
                    string.IsNullOrWhiteSpace(_localXray.LastError) ? Strings.ErrXrayStartFailed : _localXray.LastError);
            }

            runtimeState.LastSelectedServerId = server.Id;
            ApplySystemProxy(settings);
            UpdateState(isRunning: true, activeServer: server, isTunSession: false);
            return RuntimeConnectResult.Succeeded();
        }
        finally
        {
            _isTransitioning = false;
        }
    }

    public async Task PrepareForCoreUpdateAsync()
    {
        _isTransitioning = true;
        try
        {
            await StopLocalSessionIfNeededAsync();
            await StopTunSessionIfNeededAsync();
            SystemProxyService.ClearProxy();
            UpdateState(isRunning: false, activeServer: null, isTunSession: false);
        }
        finally
        {
            _isTransitioning = false;
        }
    }

    public async Task StopForShutdownAsync()
    {
        _isShuttingDown = true;
        try
        {
            await StopTunMonitorAsync();
            SystemProxyService.ClearProxy();
            _localXray.StopForShutdown();
            await _tunHelper.ShutdownAsync();
            UpdateState(isRunning: false, activeServer: null, isTunSession: false);
        }
        finally
        {
            _isShuttingDown = false;
        }
    }

    public void ApplySystemProxy(AppSettings settings)
    {
        if (settings.IsTunMode)
        {
            SystemProxyService.ClearProxy();
            return;
        }

        if (settings.IsSystemProxyEnabled)
        {
            SystemProxyService.SetProxy("127.0.0.1", settings.LocalHttpPort);
        }
        else
        {
            SystemProxyService.ClearProxy();
        }
    }

    private async Task StopLocalSessionIfNeededAsync()
    {
        if (!_localXray.IsRunning)
        {
            return;
        }

        await _localXray.StopAsync();
    }

    private async Task StopTunSessionIfNeededAsync()
    {
        await StopTunMonitorAsync();
        if (!_isTunSession)
        {
            var status = await _tunHelper.GetStatusAsync();
            if (status?.IsRunning != true)
            {
                return;
            }
        }

        await _tunHelper.StopAsync();
    }

    private void OnLocalXrayRunningChanged(object? sender, bool running)
    {
        if (running || _isTransitioning || _isShuttingDown)
        {
            return;
        }

        SystemProxyService.ClearProxy();
        UpdateState(isRunning: false, activeServer: null, isTunSession: false);
    }

    private async Task StartTunMonitorAsync()
    {
        await StopTunMonitorAsync();
        var cts = new CancellationTokenSource();
        var monitorTask = MonitorTunSessionAsync(cts, cts.Token);
        _tunMonitorCts = cts;
        _tunMonitorTask = monitorTask;
    }

    private async Task MonitorTunSessionAsync(CancellationTokenSource owner, CancellationToken cancellationToken)
    {
        var failedProbes = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                var status = await _tunHelper.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (status?.Success == true && status.IsRunning)
                {
                    failedProbes = 0;
                    continue;
                }

                failedProbes++;
                if (status?.Success == true || failedProbes >= 3)
                {
                    if (ReferenceEquals(_tunMonitorCts, owner)
                        && !_isTransitioning
                        && !_isShuttingDown
                        && _isTunSession)
                    {
                        SystemProxyService.ClearProxy();
                        UpdateState(isRunning: false, activeServer: null, isTunSession: false);
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (ReferenceEquals(_tunMonitorCts, owner)
                && !_isTransitioning
                && !_isShuttingDown
                && _isTunSession)
            {
                SystemProxyService.ClearProxy();
                UpdateState(isRunning: false, activeServer: null, isTunSession: false);
            }
        }
    }

    private async Task StopTunMonitorAsync()
    {
        var cts = _tunMonitorCts;
        var monitorTask = _tunMonitorTask;
        _tunMonitorCts = null;
        _tunMonitorTask = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            if (monitorTask is not null)
            {
                await monitorTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void UpdateState(bool isRunning, ServerEntry? activeServer, bool isTunSession)
    {
        var changed = _isRunning != isRunning
            || _isTunSession != isTunSession
            || !ReferenceEquals(_activeServer, activeServer);
        _isRunning = isRunning;
        _isTunSession = isTunSession;
        _activeServer = activeServer;
        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class RuntimeConnectResult
{
    private RuntimeConnectResult(bool success, string? errorTitle, string? errorMessage)
    {
        Success = success;
        ErrorTitle = errorTitle;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }

    public string? ErrorTitle { get; }

    public string? ErrorMessage { get; }

    public static RuntimeConnectResult Succeeded() => new(true, null, null);

    public static RuntimeConnectResult Failed(string title, string message) => new(false, title, message);
}
