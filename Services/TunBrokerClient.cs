using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Orayo;

namespace Orayo.Services;

public sealed class TunBrokerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public string LastError { get; private set; } = string.Empty;

    public async Task<bool> EnsureBrokerAvailableAsync()
    {
        var (ping, pingError) = await TrySendAsync(new TunBrokerRequest { Command = "ping" });
        if (ping?.Success == true)
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

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--broker",
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            LastError = Strings.ErrUacCancelled;
            return false;
        }
        catch (Exception ex)
        {
            LastError = Strings.ErrTunBrokerStartFailed + ex.Message;
            return false;
        }

        string? lastProbeError = pingError;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(100);
            (ping, pingError) = await TrySendAsync(new TunBrokerRequest { Command = "ping" });
            if (ping?.Success == true)
            {
                LastError = string.Empty;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(pingError))
            {
                lastProbeError = pingError;
            }
        }

        LastError = string.IsNullOrWhiteSpace(lastProbeError)
            ? Strings.ErrTunBrokerNoResponse
            : Strings.ErrTunBrokerNoResponseWith + lastProbeError;
        return false;
    }

    public async Task<TunBrokerResponse?> GetStatusAsync()
    {
        var (response, error) = await TrySendAsync(new TunBrokerRequest { Command = "status" });
        if (!string.IsNullOrWhiteSpace(error))
        {
            LastError = error;
        }

        return response;
    }

    public async Task<TunBrokerResponse?> StartAsync(string configJson)
    {
        var (response, error) = await TrySendAsync(new TunBrokerRequest
        {
            Command = "start",
            ConfigJson = configJson
        });
        if (response?.Success != true)
        {
            LastError = response?.ErrorMessage ?? error ?? Strings.ErrTunBrokerXrayFailed;
        }

        return response;
    }

    public async Task<TunBrokerResponse?> StopAsync()
    {
        var (response, error) = await TrySendAsync(new TunBrokerRequest { Command = "stop" });
        if (!string.IsNullOrWhiteSpace(error))
        {
            LastError = error;
        }

        return response;
    }

    public async Task<TunBrokerResponse?> ShutdownAsync()
    {
        var (response, error) = await TrySendAsync(new TunBrokerRequest { Command = "shutdown" });
        if (!string.IsNullOrWhiteSpace(error))
        {
            LastError = error;
        }

        return response;
    }

    private static async Task<(TunBrokerResponse? Response, string? Error)> TrySendAsync(TunBrokerRequest request)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", TunBrokerProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(200);
            client.ReadMode = PipeTransmissionMode.Message;

            var requestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions));
            await client.WriteAsync(requestBytes);
            await client.FlushAsync();

            var responseText = await ReadMessageAsync(client);
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return (null, Strings.ErrTunBrokerEmptyResponse);
            }

            return (JsonSerializer.Deserialize<TunBrokerResponse>(responseText, JsonOptions), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static async Task<string?> ReadMessageAsync(PipeStream stream)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        do
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (read <= 0)
            {
                break;
            }

            ms.Write(buffer, 0, read);
        }
        while (!stream.IsMessageComplete);

        return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray());
    }
}
