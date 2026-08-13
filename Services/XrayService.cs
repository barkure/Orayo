using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Orayo;

namespace Orayo.Services;

public class XrayService
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private IntPtr _jobHandle;
    private static readonly string ExePath = Path.Combine(
        AppContext.BaseDirectory, "Assets", "engine", "xray.exe");

    public static readonly string RulesDir = Path.Combine(
        AppContext.BaseDirectory, "Assets", "rules");

    private readonly string _configPath;
    private readonly bool _deleteConfigOnStop;

    private const int LogBufferMax = 500;

    private Process? _process;
    private StringBuilder _startupLog = new();
    private bool _collectStartupLog;
    private readonly object _startupLogLock = new();
    private readonly string[] _logBuffer = new string[LogBufferMax];
    private int _logHead;
    private int _logCount;
    private readonly object _bufferLock = new();

    public bool IsRunning => _process is { HasExited: false };

    public string LastError { get; private set; } = string.Empty;

    public event EventHandler<string>? LogReceived;
    public event EventHandler<bool>? RunningChanged;

    public XrayService(string? configPath = null, bool deleteConfigOnStop = false)
    {
        _configPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Orayo", "xray_config.json");
        _deleteConfigOnStop = deleteConfigOnStop;
    }

    public IReadOnlyList<string> GetLogBuffer()
    {
        lock (_bufferLock)
        {
            if (_logCount == 0)
            {
                return Array.Empty<string>();
            }

            var snapshot = new string[_logCount];
            if (_logCount < LogBufferMax)
            {
                Array.Copy(_logBuffer, 0, snapshot, 0, _logCount);
            }
            else
            {
                var tailCount = LogBufferMax - _logHead;
                Array.Copy(_logBuffer, _logHead, snapshot, 0, tailCount);
                Array.Copy(_logBuffer, 0, snapshot, tailCount, _logHead);
            }

            return snapshot;
        }
    }

    public void ClearLogBuffer()
    {
        lock (_bufferLock)
        {
            Array.Clear(_logBuffer, 0, _logBuffer.Length);
            _logHead = 0;
            _logCount = 0;
        }
    }

    private void AppendLog(string line)
    {
        lock (_bufferLock)
        {
            _logBuffer[_logHead] = line;
            _logHead = (_logHead + 1) % LogBufferMax;
            if (_logCount < LogBufferMax)
            {
                _logCount++;
            }
        }

        LogReceived?.Invoke(this, line);
    }

    private void BeginStartupLogCapture()
    {
        lock (_startupLogLock)
        {
            _startupLog = new StringBuilder();
            _collectStartupLog = true;
        }
    }

    private void AppendStartupLog(string line)
    {
        lock (_startupLogLock)
        {
            if (!_collectStartupLog)
            {
                return;
            }

            _startupLog.AppendLine(line);
        }
    }

    private string StopStartupLogCaptureAndRead()
    {
        lock (_startupLogLock)
        {
            _collectStartupLog = false;
            var text = _startupLog.Length > 0 ? _startupLog.ToString().Trim() : string.Empty;
            _startupLog = new StringBuilder();
            return text;
        }
    }

    private void StopStartupLogCapture()
    {
        lock (_startupLogLock)
        {
            _collectStartupLog = false;
            _startupLog = new StringBuilder();
        }
    }

    public async Task<bool> StartAsync(string configJson)
    {
        if (IsRunning)
        {
            await StopCoreAsync();
        }

        LastError = string.Empty;

        if (!File.Exists(ExePath))
        {
            LastError = string.Format(Strings.ErrXrayExeNotFound, ExePath);
            AppendLog(string.Format(Strings.LogError, LastError));
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            await File.WriteAllTextAsync(_configPath, configJson);

            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = $"run -config \"{_configPath}\"",
                WorkingDirectory = Path.GetDirectoryName(ExePath)!,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.EnvironmentVariables["XRAY_LOCATION_ASSET"] = RulesDir;

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process = process;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                AppendStartupLog(e.Data);
                AppendLog(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                AppendStartupLog(e.Data);
                AppendLog(e.Data);
            };
            process.Exited += OnProcessExited;

            BeginStartupLogCapture();
            process.Start();
            TryAttachJobObject(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            AppendLog(string.Format(Strings.LogStart, ExePath));
            AppendLog(string.Format(Strings.LogConfig, _configPath));

            // Wait until the local inbound actually accepts connections (instead of a
            // fixed delay), so startup returns as soon as the core is really ready.
            var localPortReady = await WaitForLocalPortReadyAsync(configJson);

            if (process.HasExited)
            {
                var startupLog = StopStartupLogCaptureAndRead();
                LastError = startupLog.Length > 0 ? startupLog : string.Format(Strings.ErrXrayExitImmediately, process.ExitCode);
                AppendLog(string.Format(Strings.LogStartFailed, LastError));
                DisposeExitedProcess();
                return false;
            }

            if (!localPortReady)
            {
                StopStartupLogCapture();
                LastError = Strings.ErrXrayPortNotReady;
                AppendLog(string.Format(Strings.LogStartFailed, LastError));
                DisposeExitedProcess();
                return false;
            }

            StopStartupLogCapture();
            RunningChanged?.Invoke(this, true);
            return true;
        }
        catch (Exception ex)
        {
            StopStartupLogCapture();
            LastError = ex.Message;
            AppendLog(string.Format(Strings.LogException, ex.Message));
            DisposeExitedProcess();
            return false;
        }
    }

    public async Task StopAsync()
    {
        await StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        if (_process is null)
        {
            DeleteEphemeralConfig();
            return;
        }

        var process = _process;
        _process = null;
        process.Exited -= OnProcessExited;

        try
        {
            KillProcess(process);
            await process.WaitForExitAsync();
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
            CloseJobObject();
        }

        AppendLog(Strings.LogStopped);
        DeleteEphemeralConfig();
        RunningChanged?.Invoke(this, false);
    }

    /// <summary>Terminates the xray process tree, swallowing errors on already-exited processes.</summary>
    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private void TryAttachJobObject(Process process)
    {
        CloseJobObject();

        _jobHandle = CreateJobObject(IntPtr.Zero, $"OrayoXrayJob-{Environment.ProcessId}");
        if (_jobHandle == IntPtr.Zero)
        {
            AppendLog(Strings.WarnJobObjectFailed);
            return;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        var size = (uint)Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(_jobHandle, 9, ptr, size))
            {
                AppendLog(Strings.WarnJobObjectLimit);
                CloseJobObject();
                return;
            }

            if (!AssignProcessToJobObject(_jobHandle, process.Handle))
            {
                AppendLog(Strings.WarnJobObjectAssign);
                CloseJobObject();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void CloseJobObject()
    {
        if (_jobHandle == IntPtr.Zero)
        {
            return;
        }

        CloseHandle(_jobHandle);
        _jobHandle = IntPtr.Zero;
    }

    private void DisposeExitedProcess()
    {
        var process = _process;
        if (process is null)
        {
            CloseJobObject();
            DeleteEphemeralConfig();
            return;
        }

        process.Exited -= OnProcessExited;
        _process = null;

        try
        {
            KillProcess(process);
            process.WaitForExit(500);
            process.Dispose();
        }
        catch
        {
        }
        finally
        {
            CloseJobObject();
            DeleteEphemeralConfig();
        }
    }

    private async Task<bool> WaitForLocalPortReadyAsync(string configJson)
    {
        var port = TryGetLocalInboundPort(configJson);
        if (port is null)
        {
            return true;
        }

        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (_process is { HasExited: true })
            {
                return false;
            }

            if (await TryConnectLocalPortAsync(port.Value))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static int? TryGetLocalInboundPort(string configJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (!doc.RootElement.TryGetProperty("inbounds", out var inbounds)
                || inbounds.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var inbound in inbounds.EnumerateArray())
            {
                if (inbound.TryGetProperty("port", out var port)
                    && port.ValueKind == JsonValueKind.Number
                    && port.TryGetInt32(out var portNumber))
                {
                    return portNumber;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static async Task<bool> TryConnectLocalPortAsync(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StopForShutdown()
    {
        var process = _process;
        if (process is null)
        {
            CloseJobObject();
            DeleteEphemeralConfig();
            return;
        }

        process.Exited -= OnProcessExited;
        _process = null;

        try
        {
            KillProcess(process);
            process.WaitForExit(500);
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
            CloseJobObject();
            DeleteEphemeralConfig();
        }

        AppendLog("[shutdown] xray stopped");
        RunningChanged?.Invoke(this, false);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        AppendLog(Strings.LogXrayExited);
        DeleteEphemeralConfig();
        RunningChanged?.Invoke(this, false);
    }

    private void DeleteEphemeralConfig()
    {
        if (!_deleteConfigOnStop)
        {
            return;
        }

        try
        {
            if (File.Exists(_configPath))
            {
                File.Delete(_configPath);
            }
        }
        catch
        {
        }
    }
}
