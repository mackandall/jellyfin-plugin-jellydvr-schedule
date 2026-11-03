using System;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.DvrSchedule.Web;

/// <summary>Reading what the web page and the TV app send.</summary>
internal static class WebInput
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// An optional ISO time (UTC or with an offset) that must be in the future: false when it is given
    /// but isn't one; <paramref name="utc"/> is null when it is empty.
    /// </summary>
    public static bool TryFuture(string? text, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) || parsed.UtcDateTime <= DateTime.UtcNow)
        {
            return false;
        }

        utc = parsed.UtcDateTime;
        return true;
    }
}
