using System;
using System.Threading.Tasks;
using Orayo.Models;

namespace Orayo.Services;

public sealed class RuntimeService
{
    private readonly XrayService _localXray = new();
    private readonly TunService _tunService = new();
    private readonly TunBrokerClient _tunBroker = new();
    private ServerEntry? _activeServer;
    private bool _isRunning;
    private bool _isTunSession;
    private bool _isTransitioning;
    private bool _isShuttingDown;

    public RuntimeService()
    {
        _localXray.RunningChanged += OnLocalXrayRunningChanged;
    }

    public bool IsRunning => _isRunning;

    public ServerEntry? ActiveServer => _activeServer;

    public string LastError => _localXray.LastError;

    public string TunBrokerLastError => _tunBroker.LastError;

    public event EventHandler? StateChanged;

    public async Task<bool> EnsureTunBrokerAvailableAsync()
    {
        return await _tunBroker.EnsureBrokerAvailableAsync();
    }

    public async Task<bool> EnsureTunBrokerStoppedAsync()
    {
        await _tunBroker.ShutdownAsync();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(100);
            var status = await _tunBroker.GetStatusAsync();
            if (status is null)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<RuntimeConnectResult> ConnectAsync(ServerEntry server, AppSettings settings, AppRuntimeState runtimeState)
    {
        _isTransitioning = true;
        try
        {
            if (settings.IsTunMode)
            {
                await StopLocalSessionIfNeededAsync();

                var portConflict = await PortConflictService.EnsurePortsAvailableForCurrentXrayAsync(settings.LocalSocksPort, settings.LocalHttpPort);
                if (!string.IsNullOrWhiteSpace(portConflict))
                {
                    return RuntimeConnectResult.Failed("连接失败", portConflict);
                }

                if (!_tunService.IsWintunAvailable())
                {
                    return RuntimeConnectResult.Failed("TUN 模式错误", $"找不到 wintun.dll\n路径：{_tunService.GetExpectedWintunPath()}");
                }

                var tunOutboundInterfaceName = _tunService.DetectDefaultOutboundInterfaceName();
                if (string.IsNullOrWhiteSpace(tunOutboundInterfaceName))
                {
                    return RuntimeConnectResult.Failed("TUN 模式错误", "无法确定默认出站网卡，请确认 Wi-Fi 或以太网已连接。");
                }

                if (!await _tunBroker.EnsureBrokerAvailableAsync())
                {
                    return RuntimeConnectResult.Failed("TUN 模式错误", "无法启动 TUN 权限代理。");
                }

                SystemProxyService.ClearProxy();

                var config = XrayConfigBuilder.Build(server, settings, tunOutboundInterfaceName);
                var response = await _tunBroker.StartAsync(config, server.Host);
                if (response?.Success != true)
                {
                    SystemProxyService.ClearProxy();
                    UpdateState(isRunning: false, activeServer: null, isTunSession: true);
                    return RuntimeConnectResult.Failed(
                        response?.ErrorTitle ?? "连接失败",
                        response?.ErrorMessage ?? "TUN 启动失败。");
                }

                var tunReady = await WaitForTunReadyAsync();
                if (!tunReady)
                {
                    await _tunBroker.StopAsync();
                    SystemProxyService.ClearProxy();
                    UpdateState(isRunning: false, activeServer: null, isTunSession: true);
                    return RuntimeConnectResult.Failed(
                        "TUN 模式错误",
                        string.IsNullOrWhiteSpace(_tunBroker.LastError)
                            ? "TUN 启动后未检测到 xray-tun 网卡或运行进程。"
                            : _tunBroker.LastError);
                }

                SystemProxyService.ClearProxy();
                runtimeState.LastSelectedServerId = server.Id;
                UpdateState(isRunning: true, activeServer: server, isTunSession: true);
                return RuntimeConnectResult.Succeeded();
            }

            await StopTunSessionIfNeededAsync();

            var localPortConflict = await PortConflictService.EnsurePortsAvailableForCurrentXrayAsync(settings.LocalSocksPort, settings.LocalHttpPort);
            if (!string.IsNullOrWhiteSpace(localPortConflict))
            {
                return RuntimeConnectResult.Failed("连接失败", localPortConflict);
            }

            var localConfig = XrayConfigBuilder.Build(server, settings);
            var ok = await _localXray.StartAsync(localConfig);
            if (!ok)
            {
                SystemProxyService.ClearProxy();
                UpdateState(isRunning: false, activeServer: null, isTunSession: false);
                return RuntimeConnectResult.Failed(
                    "连接失败",
                    string.IsNullOrWhiteSpace(_localXray.LastError) ? "xray 启动失败。" : _localXray.LastError);
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
            SystemProxyService.ClearProxy();
            _localXray.StopForShutdown();
            await EnsureTunBrokerStoppedAsync();
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
        if (!_isTunSession && !await IsBrokerRunningAsync())
        {
            return;
        }

        await EnsureTunBrokerStoppedAsync();
    }

    private async Task<bool> IsBrokerRunningAsync()
    {
        var status = await _tunBroker.GetStatusAsync();
        return status?.IsRunning == true;
    }

    private async Task<bool> WaitForTunReadyAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var status = await _tunBroker.GetStatusAsync();
            if (status?.IsRunning == true && _tunService.IsTunInterfaceActive())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private void OnLocalXrayRunningChanged(object? sender, bool running)
    {
        if (running || _isTransitioning || _isShuttingDown || _isTunSession)
        {
            return;
        }

        SystemProxyService.ClearProxy();
        UpdateState(isRunning: false, activeServer: null, isTunSession: false);
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
