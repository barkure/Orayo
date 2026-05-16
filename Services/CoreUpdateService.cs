using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Orayo.Services;

public static class CoreUpdateService
{
    private static readonly string EngineDir = Path.Combine(AppContext.BaseDirectory, "Assets", "engine");
    private static readonly string RulesDir = Path.Combine(AppContext.BaseDirectory, "Assets", "rules");
    private static readonly string XrayExePath = Path.Combine(EngineDir, "xray.exe");
    private const string XrayWindows64Url = "https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip";
    private const string GeoipUrl = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat";
    private const string GeositeUrl = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat";
    private static readonly Regex VersionRegex = new(@"Xray\s+(?<version>[0-9.]+).*?\)\s+(?<commit>[0-9a-f]{7,})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<XrayVersionInfo> GetXrayVersionInfoAsync()
    {
        if (!File.Exists(XrayExePath))
        {
            return new XrayVersionInfo("未找到 xray.exe", string.Empty, null, "未找到 xray.exe");
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = XrayExePath,
            Arguments = "version",
            WorkingDirectory = EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (process is null)
        {
            return new XrayVersionInfo("无法启动 xray.exe", string.Empty, null, "无法启动 xray.exe");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = (await outputTask).Trim();
        if (!string.IsNullOrWhiteSpace(output))
        {
            var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            var match = VersionRegex.Match(firstLine);
            if (match.Success)
            {
                var version = match.Groups["version"].Value;
                var commit = match.Groups["commit"].Value;
                return new XrayVersionInfo(version, commit, $"https://github.com/XTLS/Xray-core/releases/tag/v{version}", firstLine);
            }

            return new XrayVersionInfo(firstLine, string.Empty, null, firstLine);
        }

        var error = (await errorTask).Trim();
        return new XrayVersionInfo(string.IsNullOrWhiteSpace(error) ? "未知版本" : error, string.Empty, null, error);
    }

    public static async Task UpdateXrayCoreAsync()
    {
        Directory.CreateDirectory(EngineDir);
        var tempDir = Path.Combine(Path.GetTempPath(), "Orayo", "xray-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "xray.zip");

        try
        {
            await DownloadFileAsync(XrayWindows64Url, zipPath);
            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);
            var extractedXray = Directory.GetFiles(tempDir, "xray.exe", SearchOption.AllDirectories)[0];
            File.Copy(extractedXray, XrayExePath, overwrite: true);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    public static async Task UpdateGeofilesAsync()
    {
        Directory.CreateDirectory(RulesDir);
        await DownloadFileAsync(GeoipUrl, Path.Combine(RulesDir, "geoip.dat"));
        await DownloadFileAsync(GeositeUrl, Path.Combine(RulesDir, "geosite.dat"));
    }

    private static async Task DownloadFileAsync(string url, string path)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Orayo");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(path);
        await input.CopyToAsync(output);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}

public sealed record XrayVersionInfo(string Version, string Commit, string? ReleaseUrl, string Raw);
