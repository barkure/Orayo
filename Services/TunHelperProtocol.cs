using System;

namespace Orayo.Services;

public sealed class TunHelperLaunchOptions
{
    public required string PipeName { get; init; }

    public required string SessionToken { get; init; }

    public required string AllowedUserSid { get; init; }

    public required int ParentProcessId { get; init; }

    public required long ParentStartTimeUtcTicks { get; init; }

    public static bool TryParse(string[] args, out TunHelperLaunchOptions? options)
    {
        options = null;
        var index = Array.FindIndex(args, arg =>
            string.Equals(arg, TunHelperProtocol.HelperArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || args.Length <= index + 5)
        {
            return false;
        }

        var pipeName = args[index + 1];
        var sessionToken = args[index + 2];
        var allowedUserSid = args[index + 3];
        if (!pipeName.StartsWith(TunHelperProtocol.PipeNamePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(pipeName[TunHelperProtocol.PipeNamePrefix.Length..], "N", out _)
            || !IsValidSessionToken(sessionToken)
            || !IsValidSid(allowedUserSid)
            || !int.TryParse(args[index + 4], out var parentProcessId)
            || parentProcessId <= 0
            || !long.TryParse(args[index + 5], out var parentStartTimeUtcTicks)
            || parentStartTimeUtcTicks <= 0)
        {
            return false;
        }

        options = new TunHelperLaunchOptions
        {
            PipeName = pipeName,
            SessionToken = sessionToken,
            AllowedUserSid = allowedUserSid,
            ParentProcessId = parentProcessId,
            ParentStartTimeUtcTicks = parentStartTimeUtcTicks
        };
        return true;
    }

    private static bool IsValidSessionToken(string sessionToken)
    {
        if (sessionToken.Length != 64)
        {
            return false;
        }

        foreach (var character in sessionToken)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidSid(string sid)
    {
        try
        {
            _ = new System.Security.Principal.SecurityIdentifier(sid);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public static class TunHelperProtocol
{
    public const string HelperArgument = "--tun-helper";
    public const string PipeNamePrefix = "Orayo.TunHelper.";

    public static bool IsHelperInvocation(string[] args)
    {
        return Array.Exists(args, arg =>
            string.Equals(arg, HelperArgument, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class TunHelperRequest
{
    public string SessionToken { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public string? ConfigJson { get; set; }
}

public sealed class TunHelperResponse
{
    public bool Success { get; set; }

    public bool IsRunning { get; set; }

    public string? ErrorTitle { get; set; }

    public string? ErrorMessage { get; set; }
}
