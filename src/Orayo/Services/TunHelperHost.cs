using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Orayo.Helpers;

namespace Orayo.Services;

public sealed class TunHelperHost
{
    private const int MaxMessageBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly TunHelperLaunchOptions _options;
    private readonly XrayService _xray;
    private readonly SecurityIdentifier _allowedUser;
    private readonly string? _helperExecutablePath;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _shutdownRequested;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        TokenAccessLevels desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    public TunHelperHost(TunHelperLaunchOptions options)
    {
        _options = options;
        _allowedUser = new SecurityIdentifier(options.AllowedUserSid);
        _helperExecutablePath = NormalizeExecutablePath(Environment.ProcessPath);
        var configPath = Path.Combine(Path.GetTempPath(), options.PipeName + ".json");
        _xray = new XrayService(configPath, deleteConfigOnStop: true);
    }

    public async Task RunAsync()
    {
        if (!AdminHelper.IsAdministrator() || !IsExpectedParentProcess())
        {
            return;
        }

        var parentWatch = WatchParentProcessAsync();
        try
        {
            while (!_shutdownRequested && !_shutdown.IsCancellationRequested)
            {
                using var server = CreatePipeServer();
                try
                {
                    await server.WaitForConnectionAsync(_shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                TunHelperResponse response;
                try
                {
                    if (!IsAuthorizedClient(server))
                    {
                        response = Fail(Strings.ErrTunModeError, Strings.ErrTunHelperUnauthorized);
                    }
                    else
                    {
                        var requestText = await ReadMessageAsync(server, _shutdown.Token);
                        response = await HandleRequestAsync(requestText);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (InvalidDataException)
                {
                    response = Fail(Strings.ErrTunModeError, Strings.ErrTunHelperInvalidRequest);
                }
                catch
                {
                    continue;
                }

                try
                {
                    var responseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, JsonOptions));
                    await server.WriteAsync(responseBytes);
                    await server.FlushAsync();
                }
                catch
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _shutdown.Cancel();
            _xray.StopForShutdown();
            try
            {
                await parentWatch;
            }
            catch
            {
            }
        }
    }

    private NamedPipeServerStream CreatePipeServer()
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        pipeSecurity.AddAccessRule(new PipeAccessRule(
            _allowedUser,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        var helperUser = WindowsIdentity.GetCurrent().User;
        if (helperUser is not null && !helperUser.Equals(_allowedUser))
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                helperUser,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            pipeSecurity.SetOwner(helperUser);
        }

        return NamedPipeServerStreamAcl.Create(
            _options.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous,
            0,
            0,
            pipeSecurity);
    }

    private async Task<TunHelperResponse> HandleRequestAsync(string? requestText)
    {
        if (string.IsNullOrWhiteSpace(requestText))
        {
            return Fail(Strings.ErrTunModeError, Strings.ErrTunHelperInvalidRequest);
        }

        TunHelperRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<TunHelperRequest>(requestText, JsonOptions);
        }
        catch
        {
            return Fail(Strings.ErrTunModeError, Strings.ErrTunHelperInvalidRequest);
        }

        if (request is null || !TokenMatches(request.SessionToken))
        {
            return Fail(Strings.ErrTunModeError, Strings.ErrTunHelperUnauthorized);
        }

        return request.Command switch
        {
            "ping" => Ok(),
            "status" => Ok(),
            "start" => await StartAsync(request),
            "stop" => await StopAsync(),
            "shutdown" => await ShutdownAsync(),
            _ => Fail(Strings.ErrTunModeError, Strings.ErrTunHelperInvalidRequest)
        };
    }

    private async Task<TunHelperResponse> StartAsync(TunHelperRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConfigJson))
        {
            return Fail(Strings.ErrTunModeError, Strings.ErrTunHelperInvalidRequest);
        }

        var started = await _xray.StartAsync(request.ConfigJson);
        if (!started)
        {
            return Fail(
                Strings.ErrTunModeError,
                string.IsNullOrWhiteSpace(_xray.LastError) ? Strings.ErrTunStartFailed : _xray.LastError);
        }

        return Ok();
    }

    private async Task<TunHelperResponse> StopAsync()
    {
        await _xray.StopAsync();
        return Ok();
    }

    private async Task<TunHelperResponse> ShutdownAsync()
    {
        var response = await StopAsync();
        _shutdownRequested = true;
        return response;
    }

    private async Task WatchParentProcessAsync()
    {
        try
        {
            using var parent = Process.GetProcessById(_options.ParentProcessId);
            if (!IsExpectedParentProcess(parent))
            {
                _shutdown.Cancel();
                return;
            }

            await parent.WaitForExitAsync(_shutdown.Token);
            if (!_shutdown.IsCancellationRequested)
            {
                _shutdown.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            _shutdown.Cancel();
        }
    }

    private bool IsAuthorizedClient(NamedPipeServerStream server)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientProcessId)
                || clientProcessId != (uint)_options.ParentProcessId)
            {
                return false;
            }

            using var client = Process.GetProcessById((int)clientProcessId);
            return IsExpectedParentProcess(client);
        }
        catch
        {
            return false;
        }
    }

    private bool IsExpectedParentProcess()
    {
        try
        {
            using var parent = Process.GetProcessById(_options.ParentProcessId);
            return IsExpectedParentProcess(parent);
        }
        catch
        {
            return false;
        }
    }

    private bool IsExpectedParentProcess(Process parent)
    {
        try
        {
            if (parent.HasExited
                || parent.StartTime.ToUniversalTime().Ticks != _options.ParentStartTimeUtcTicks)
            {
                return false;
            }

            var parentExecutablePath = NormalizeExecutablePath(parent.MainModule?.FileName);
            if (string.IsNullOrWhiteSpace(_helperExecutablePath)
                || !string.Equals(parentExecutablePath, _helperExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!OpenProcessToken(parent.Handle, TokenAccessLevels.Query, out var tokenHandle))
            {
                return false;
            }

            using (tokenHandle)
            using (var identity = new WindowsIdentity(tokenHandle.DangerousGetHandle()))
            {
                return identity.User?.Equals(_allowedUser) == true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeExecutablePath(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private bool TokenMatches(string candidate)
    {
        if (candidate.Length != _options.SessionToken.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate),
            Encoding.UTF8.GetBytes(_options.SessionToken));
    }

    private TunHelperResponse Ok()
    {
        return new TunHelperResponse
        {
            Success = true,
            IsRunning = _xray.IsRunning
        };
    }

    private static TunHelperResponse Fail(string title, string message)
    {
        return new TunHelperResponse
        {
            Success = false,
            ErrorTitle = title,
            ErrorMessage = message
        };
    }

    private static async Task<string?> ReadMessageAsync(PipeStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var output = new MemoryStream();
        do
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            if (output.Length > MaxMessageBytes)
            {
                throw new InvalidDataException("TUN helper request exceeded the allowed size.");
            }
        }
        while (!stream.IsMessageComplete);

        return output.Length == 0 ? null : Encoding.UTF8.GetString(output.ToArray());
    }
}
