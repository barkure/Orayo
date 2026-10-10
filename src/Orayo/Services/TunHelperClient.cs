using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Orayo.Services;

public sealed class TunHelperClient
{
    private const int MaxMessageBytes = 2 * 1024 * 1024;
    private const int PipeConnectTimeoutMilliseconds = 1000;
    private static readonly TimeSpan HelperStartupTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private string? _pipeName;
    private string? _sessionToken;

    public string LastError { get; private set; } = string.Empty;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var response = await TrySendAsync(
            new TunHelperRequest { Command = "ping" },
            updateLastError: false,
            cancellationToken);
        return response?.Success == true;
    }

    public async Task<bool> EnsureAvailableAsync()
    {
        await _launchGate.WaitAsync();
        try
        {
            if (await IsAvailableAsync())
            {
                LastError = string.Empty;
                return true;
            }

            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                LastError = Strings.ErrCannotDetermineExePath;
                return false;
            }

            using var identity = WindowsIdentity.GetCurrent();
            var userSid = identity.User?.Value;
            if (string.IsNullOrWhiteSpace(userSid))
            {
                LastError = Strings.ErrCannotDetermineUserSid;
                return false;
            }

            _pipeName = TunHelperProtocol.PipeNamePrefix + Guid.NewGuid().ToString("N");
            _sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var currentProcess = Process.GetCurrentProcess();

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                startInfo.ArgumentList.Add(TunHelperProtocol.HelperArgument);
                startInfo.ArgumentList.Add(_pipeName);
                startInfo.ArgumentList.Add(_sessionToken);
                startInfo.ArgumentList.Add(userSid);
                startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
                startInfo.ArgumentList.Add(currentProcess.StartTime.ToUniversalTime().Ticks.ToString());

                using var helperProcess = Process.Start(startInfo);
                if (helperProcess is null)
                {
                    LastError = Strings.ErrCannotStartTunHelper;
                    ClearSession();
                    return false;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                LastError = Strings.ErrUacCancelled;
                ClearSession();
                return false;
            }
            catch (Exception ex)
            {
                LastError = string.Format(Strings.ErrTunHelperStartFailed, ex.Message);
                ClearSession();
                return false;
            }

            using var startupTimeout = new CancellationTokenSource(HelperStartupTimeout);
            try
            {
                while (!startupTimeout.IsCancellationRequested)
                {
                    await Task.Delay(100, startupTimeout.Token);
                    if (await IsAvailableAsync(startupTimeout.Token))
                    {
                        LastError = string.Empty;
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) when (startupTimeout.IsCancellationRequested)
            {
            }

            LastError = Strings.ErrTunHelperNoResponse;
            ClearSession();
            return false;
        }
        finally
        {
            _launchGate.Release();
        }
    }

    public Task<TunHelperResponse?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return TrySendAsync(new TunHelperRequest { Command = "status" }, cancellationToken: cancellationToken);
    }

    public Task<TunHelperResponse?> StartAsync(string configJson, CancellationToken cancellationToken = default)
    {
        return TrySendAsync(new TunHelperRequest
        {
            Command = "start",
            ConfigJson = configJson
        }, cancellationToken: cancellationToken);
    }

    public Task<TunHelperResponse?> StopAsync(CancellationToken cancellationToken = default)
    {
        return TrySendAsync(new TunHelperRequest { Command = "stop" }, cancellationToken: cancellationToken);
    }

    public async Task ShutdownAsync()
    {
        await TrySendAsync(new TunHelperRequest { Command = "shutdown" }, updateLastError: false);
        ClearSession();
    }

    private async Task<TunHelperResponse?> TrySendAsync(
        TunHelperRequest request,
        bool updateLastError = true,
        CancellationToken cancellationToken = default)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var pipeName = _pipeName;
            var sessionToken = _sessionToken;
            if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(sessionToken))
            {
                return null;
            }

            request.SessionToken = sessionToken;
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await client.ConnectAsync(PipeConnectTimeoutMilliseconds, cancellationToken);
                client.ReadMode = PipeTransmissionMode.Message;

                using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                responseTimeout.CancelAfter(ResponseTimeout);
                var operationToken = responseTimeout.Token;
                var requestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions));
                await client.WriteAsync(requestBytes, operationToken);
                await client.FlushAsync(operationToken);

                var responseText = await ReadMessageAsync(client, operationToken);
                if (string.IsNullOrWhiteSpace(responseText))
                {
                    if (updateLastError)
                    {
                        LastError = Strings.ErrTunHelperEmptyResponse;
                    }
                    return null;
                }

                var response = JsonSerializer.Deserialize<TunHelperResponse>(responseText, JsonOptions);
                if (updateLastError && response?.Success != true)
                {
                    LastError = response?.ErrorMessage ?? Strings.ErrCannotStartTunHelper;
                }
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (updateLastError)
                {
                    LastError = ex.Message;
                }
                return null;
            }
        }
        finally
        {
            _requestGate.Release();
        }
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
                throw new InvalidDataException("TUN helper response exceeded the allowed size.");
            }
        }
        while (!stream.IsMessageComplete);

        return output.Length == 0 ? null : Encoding.UTF8.GetString(output.ToArray());
    }

    private void ClearSession()
    {
        _pipeName = null;
        _sessionToken = null;
    }
}
