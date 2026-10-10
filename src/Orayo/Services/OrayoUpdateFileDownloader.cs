using System.Net;
using System.Net.Http;
using Velopack.Sources;

namespace Orayo.Services;

internal sealed class OrayoUpdateFileDownloader : HttpClientFileDownloader
{
    private readonly bool _useSystemProxy;
    private readonly int _localHttpPort;

    public OrayoUpdateFileDownloader(bool useSystemProxy, int localHttpPort)
    {
        _useSystemProxy = useSystemProxy;
        _localHttpPort = localHttpPort;
    }

    protected override HttpClientHandler CreateHttpClientHandler()
    {
        var handler = base.CreateHttpClientHandler();
        if (_useSystemProxy)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy("127.0.0.1", _localHttpPort);
        }
        else
        {
            handler.UseProxy = false;
            handler.Proxy = null;
        }

        return handler;
    }
}
