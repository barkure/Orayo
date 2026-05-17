using System;
using System.IO;

namespace Orayo.Services;

public sealed class TunService
{
    private readonly string _engineDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "engine");

    public bool IsWintunAvailable()
    {
        var wintunPath = Path.Combine(_engineDirectory, "wintun.dll");
        return File.Exists(wintunPath);
    }

    public string GetExpectedWintunPath() => Path.Combine(_engineDirectory, "wintun.dll");
}
