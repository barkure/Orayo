using System;

namespace Orayo.Models;

/// <summary>
/// A subscription source. The raw payload downloaded from <see cref="Url"/> is kept
/// as the single source of truth; the node list shown in the UI is only a projection
/// of it (re-parsed on every refresh, so fixing a parser bug self-heals on next update).
/// </summary>
public class Subscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = string.Empty;
    public string Remarks { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string UserAgent { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; } = DateTime.MinValue;
    public string? RawPayload { get; set; }
}