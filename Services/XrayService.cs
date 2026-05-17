using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Orayo", "xray_config.json");

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
            LastError = $"找不到 xray.exe\n路径：{ExePath}";
            AppendLog("[错误] " + LastError);
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            await File.WriteAllTextAsync(ConfigPath, configJson);

            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = $"run -config \"{ConfigPath}\"",
                WorkingDirectory = Path.GetDirectoryName(ExePath)!,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.EnvironmentVariables["XRAY_LOCATION_ASSET"] = RulesDir;

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                AppendStartupLog(e.Data);
                AppendLog(e.Data);
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                AppendStartupLog(e.Data);
                AppendLog(e.Data);
            };
            _process.Exited += OnProcessExited;

            BeginStartupLogCapture();
            _process.Start();
            TryAttachJobObject(_process);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            AppendLog($"[启动] {ExePath}");
            AppendLog($"[配置] {ConfigPath}");

            await Task.Delay(800);

            if (_process.HasExited)
            {
                var startupLog = StopStartupLogCaptureAndRead();
                LastError = startupLog.Length > 0 ? startupLog : $"xray 立即退出（退出码 {_process.ExitCode}）";
                AppendLog("[错误] 启动失败：" + LastError);
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
            AppendLog("[异常] " + ex.Message);
            DisposeExitedProcess();
            return false;
        }
    }

    public async Task StopAsync()
    {
        await StopCoreAsync();
        await FlushSystemDnsCacheAsync();
    }

    private async Task StopCoreAsync()
    {
        if (_process is null)
        {
            return;
        }

        _process.Exited -= OnProcessExited;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            await _process.WaitForExitAsync();
        }
        catch
        {
        }
        finally
        {
            _process.Dispose();
            _process = null;
            CloseJobObject();
        }

        AppendLog("[已停止]");
        RunningChanged?.Invoke(this, false);
    }

    private void TryAttachJobObject(Process process)
    {
        CloseJobObject();

        _jobHandle = CreateJobObject(IntPtr.Zero, $"OrayoXrayJob-{Environment.ProcessId}");
        if (_jobHandle == IntPtr.Zero)
        {
            AppendLog("[警告] 未能创建作业对象，无法绑定进程生命周期。");
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
                AppendLog("[警告] 无法设置作业对象限制，xray 无法跟随退出。");
                CloseJobObject();
                return;
            }

            if (!AssignProcessToJobObject(_jobHandle, process.Handle))
            {
                AppendLog("[警告] 无法将 xray 绑定到作业对象。");
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
            return;
        }

        process.Exited -= OnProcessExited;
        _process = null;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(500);
            }

            process.Dispose();
        }
        catch
        {
        }
        finally
        {
            CloseJobObject();
        }
    }

    private async Task FlushSystemDnsCacheAsync()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(); } catch { } }
        }
        catch
        {
        }
    }

    public void StopForShutdown()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        process.Exited -= OnProcessExited;
        _process = null;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(500);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }

        AppendLog("[shutdown] xray stopped");
        RunningChanged?.Invoke(this, false);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        AppendLog("[xray 进程已退出]");
        RunningChanged?.Invoke(this, false);
    }
}

