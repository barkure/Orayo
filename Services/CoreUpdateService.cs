using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orayo.Services;

public static class CoreUpdateService
{
    private static readonly string EngineDir = Path.Combine(AppContext.BaseDirectory, "Assets", "engine");
    private static readonly string RulesDir = Path.Combine(AppContext.BaseDirectory, "Assets", "rules");
    private static readonly string XrayExePath = Path.Combine(EngineDir, "xray.exe");
    private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orayo");
    private static readonly string VelopackPackagesDir = Path.Combine(DataDir, "packages");
    private static readonly string PendingUpdateDir = Path.Combine(DataDir, "pending-update");
    private static readonly string PendingUpdateManifestPath = Path.Combine(PendingUpdateDir, "pending-xray-update.json");
    private const string XrayWindows64Url = "https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip";
    private const string GeoipUrl = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat";
    private const string GeositeUrl = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(300);
    private static readonly Regex VersionRegex = new(@"Xray\s+(?<version>[0-9.]+).*?\)\s+(?<commit>[0-9a-f]{7,})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = DownloadTimeout
    };

    private sealed class PendingXrayUpdateManifest
    {
        public string? XrayExePath { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    public static async Task<XrayVersionInfo> GetXrayVersionInfoAsync()
    {
        if (!File.Exists(XrayExePath))
        {
            return new XrayVersionInfo("未找到 xray.exe", string.Empty, null, "未找到 xray.exe");
        }

        return await ReadXrayVersionInfoAsync(XrayExePath);
    }

    private static async Task<XrayVersionInfo> ReadXrayVersionInfoAsync(string exePath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "version",
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? EngineDir,
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

    public sealed class StagedXrayCoreUpdate : IDisposable
    {
        internal StagedXrayCoreUpdate(string tempDir, string xrayExePath)
        {
            TempDir = tempDir;
            XrayExePath = xrayExePath;
        }

        internal string TempDir { get; }
        internal string XrayExePath { get; }

        public void Dispose()
        {
            TryDeleteDirectory(TempDir);
        }
    }

    public sealed class StagedGeofilesUpdate : IDisposable
    {
        internal StagedGeofilesUpdate(string tempDir, string geoipPath, string geositePath)
        {
            TempDir = tempDir;
            GeoipPath = geoipPath;
            GeositePath = geositePath;
        }

        internal string TempDir { get; }
        internal string GeoipPath { get; }
        internal string GeositePath { get; }

        public void Dispose()
        {
            TryDeleteDirectory(TempDir);
        }
    }

    public static async Task<StagedXrayCoreUpdate> StageXrayCoreUpdateAsync()
    {
        Directory.CreateDirectory(EngineDir);
        var tempDir = Path.Combine(Path.GetTempPath(), "Orayo", "xray-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "xray.zip");

        try
        {
            await DownloadFileAsync(XrayWindows64Url, zipPath);
            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);
            var extractedXray = Directory.GetFiles(tempDir, "xray.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(extractedXray))
            {
                throw new InvalidOperationException("Xray 更新包中未找到 xray.exe。");
            }

            if (File.Exists(XrayExePath))
            {
                var currentVersion = await ReadXrayVersionInfoAsync(XrayExePath);
                var extractedVersion = await ReadXrayVersionInfoAsync(extractedXray);
                if (CompareVersionStrings(extractedVersion.Version, currentVersion.Version) < 0)
                {
                    throw new InvalidOperationException($"检测到更新版本 {extractedVersion.Version} 低于当前版本 {currentVersion.Version}，已跳过更新");
                }
            }

            return new StagedXrayCoreUpdate(tempDir, extractedXray);
        }
        catch
        {
            TryDeleteDirectory(tempDir);
            throw;
        }
    }

    public static void ApplyXrayCoreUpdate(StagedXrayCoreUpdate update)
    {
        Directory.CreateDirectory(EngineDir);
        ReplaceFile(update.XrayExePath, XrayExePath);
    }

    public static void StagePendingXrayCoreUpdate(StagedXrayCoreUpdate update)
    {
        Directory.CreateDirectory(PendingUpdateDir);
        var pendingXrayPath = Path.Combine(PendingUpdateDir, "xray.exe");
        ReplaceFile(update.XrayExePath, pendingXrayPath);
        var manifest = new PendingXrayUpdateManifest
        {
            XrayExePath = pendingXrayPath,
            CreatedAt = DateTimeOffset.Now
        };
        File.WriteAllText(PendingUpdateManifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    public static bool TryApplyPendingXrayCoreUpdate()
    {
        try
        {
            if (!File.Exists(PendingUpdateManifestPath))
            {
                return false;
            }

            var manifest = JsonSerializer.Deserialize<PendingXrayUpdateManifest>(File.ReadAllText(PendingUpdateManifestPath), JsonOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.XrayExePath) || !File.Exists(manifest.XrayExePath))
            {
                ClearPendingXrayCoreUpdate();
                return false;
            }

            Directory.CreateDirectory(EngineDir);
            ReplaceFile(manifest.XrayExePath, XrayExePath);
            ClearPendingXrayCoreUpdate();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void CleanupVelopackPackages()
    {
        try
        {
            if (!Directory.Exists(VelopackPackagesDir))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(VelopackPackagesDir))
            {
                var fileName = Path.GetFileName(file);
                if (string.Equals(fileName, ".betaId", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, ".velopack_lock", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryDeleteFile(file);
            }

            foreach (var directory in Directory.GetDirectories(VelopackPackagesDir))
            {
                TryDeleteDirectory(directory);
            }
        }
        catch
        {
        }
    }

    public static async Task<StagedGeofilesUpdate> StageGeofilesUpdateAsync()
    {
        Directory.CreateDirectory(RulesDir);
        var tempDir = Path.Combine(Path.GetTempPath(), "Orayo", "geofiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var stagedGeoip = Path.Combine(tempDir, "geoip.dat");
        var stagedGeosite = Path.Combine(tempDir, "geosite.dat");

        try
        {
            await DownloadFileAsync(GeoipUrl, stagedGeoip);
            await DownloadFileAsync(GeositeUrl, stagedGeosite);
            return new StagedGeofilesUpdate(tempDir, stagedGeoip, stagedGeosite);
        }
        catch
        {
            TryDeleteDirectory(tempDir);
            throw;
        }
    }

    public static void ApplyGeofilesUpdate(StagedGeofilesUpdate update)
    {
        Directory.CreateDirectory(RulesDir);
        var geoipPath = Path.Combine(RulesDir, "geoip.dat");
        var geositePath = Path.Combine(RulesDir, "geosite.dat");
        var geoipBackup = geoipPath + "." + Guid.NewGuid().ToString("N") + ".bak";
        var geositeBackup = geositePath + "." + Guid.NewGuid().ToString("N") + ".bak";

        try
        {
            BackupFile(geoipPath, geoipBackup);
            BackupFile(geositePath, geositeBackup);
            ReplaceFile(update.GeoipPath, geoipPath);
            ReplaceFile(update.GeositePath, geositePath);
        }
        catch
        {
            RestoreBackup(geoipBackup, geoipPath);
            RestoreBackup(geositeBackup, geositePath);
            throw;
        }
        finally
        {
            TryDeleteFile(geoipBackup);
            TryDeleteFile(geositeBackup);
        }
    }

    private static async Task DownloadFileAsync(string url, string path)
    {
        if (!HttpClient.DefaultRequestHeaders.UserAgent.ToString().Contains("Orayo", StringComparison.OrdinalIgnoreCase))
        {
            HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Orayo");
        }

        using var cts = new CancellationTokenSource(DownloadTimeout);
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cts.Token);
        await using var output = File.Create(path);
        await input.CopyToAsync(output, cts.Token);
        if (output.Length == 0)
        {
            throw new InvalidOperationException("下载文件为空。");
        }
    }

    private static void ReplaceFile(string sourcePath, string targetPath)
    {
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void BackupFile(string sourcePath, string backupPath)
    {
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, backupPath, overwrite: true);
        }
    }

    private static void RestoreBackup(string backupPath, string targetPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, targetPath, overwrite: true);
            }
        }
        catch
        {
        }
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void ClearPendingXrayCoreUpdate()
    {
        TryDeleteFile(PendingUpdateManifestPath);
        TryDeleteDirectory(PendingUpdateDir);
    }

    private static int CompareVersionStrings(string left, string right)
    {
        if (!TryParseVersion(left, out var leftParts) || !TryParseVersion(right, out var rightParts))
        {
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        var length = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < length; i++)
        {
            var leftValue = i < leftParts.Length ? leftParts[i] : 0;
            var rightValue = i < rightParts.Length ? rightParts[i] : 0;
            var comparison = leftValue.CompareTo(rightValue);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static bool TryParseVersion(string value, out int[] parts)
    {
        parts = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var tokens = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var parsed = new int[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!int.TryParse(tokens[i], out parsed[i]))
            {
                parts = Array.Empty<int>();
                return false;
            }
        }

        parts = parsed;
        return true;
    }
}

public sealed record XrayVersionInfo(string Version, string Commit, string? ReleaseUrl, string Raw);
