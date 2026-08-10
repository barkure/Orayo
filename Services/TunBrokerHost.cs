using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Orayo;

namespace Orayo.Services;

public sealed class TunBrokerHost
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly XrayService _xray = new();
    private bool _shutdownRequested;

    public async Task RunAsync()
    {
        while (!_shutdownRequested)
        {
            using var server = CreatePipeServer();

            await server.WaitForConnectionAsync();

            server.ReadMode = PipeTransmissionMode.Message;
            var requestText = await ReadMessageAsync(server);
            var response = await HandleRequestAsync(requestText);
            var responseText = JsonSerializer.Serialize(response, JsonOptions);
            var responseBytes = Encoding.UTF8.GetBytes(responseText);
            await server.WriteAsync(responseBytes);
            await server.FlushAsync();
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        var pipeSecurity = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                currentUser,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }

        var server = NamedPipeServerStreamAcl.Create(
            TunBrokerProtocol.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous,
            0,
            0,
            pipeSecurity);
        return server;
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

    private async Task<TunBrokerResponse> HandleRequestAsync(string? requestText)
    {
        if (string.IsNullOrWhiteSpace(requestText))
        {
            return Fail(Strings.ErrTunBrokerError, Strings.ErrRequestEmpty);
        }

        TunBrokerRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<TunBrokerRequest>(requestText, JsonOptions);
        }
        catch (Exception ex)
        {
            return Fail(Strings.ErrTunBrokerError, ex.Message);
        }

        if (request is null)
        {
            return Fail(Strings.ErrTunBrokerError, Strings.ErrRequestInvalid);
        }

        return request.Command switch
        {
            "ping" => Ok(),
            "status" => Ok(),
            "start" => await StartAsync(request),
            "stop" => await StopAsync(),
            "shutdown" => await ShutdownAsync(),
            _ => Fail(Strings.ErrTunBrokerError, string.Format(Strings.ErrUnknownCommand, request.Command))
        };
    }

    private async Task<TunBrokerResponse> StartAsync(TunBrokerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConfigJson))
        {
            return Fail(Strings.ErrTunBrokerError, Strings.ErrMissingConfig);
        }

        var ok = await _xray.StartAsync(request.ConfigJson);
        if (!ok)
        {
            return Fail(Strings.ErrConnectionFailed, string.IsNullOrWhiteSpace(_xray.LastError) ? Strings.ErrXrayStartFailed : _xray.LastError);
        }
        return Ok();
    }

    private async Task<TunBrokerResponse> StopAsync()
    {
        await _xray.StopAsync();
        return Ok();
    }

    private async Task<TunBrokerResponse> ShutdownAsync()
    {
        await StopAsync();
        _shutdownRequested = true;
        return Ok();
    }

    private TunBrokerResponse Ok()
    {
        return new TunBrokerResponse
        {
            Success = true,
            IsRunning = _xray.IsRunning
        };
    }

    private static TunBrokerResponse Fail(string title, string message)
    {
        return new TunBrokerResponse
        {
            Success = false,
            ErrorTitle = title,
            ErrorMessage = message
        };
    }
}
