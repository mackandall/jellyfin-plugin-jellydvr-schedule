using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>
/// Takes Episode History's guide marks ("↪ ", "🟧 ", "💾 ", "✓ ") off a guide entry before it is
/// scheduled. Jellyfin copies the entry's episode title into the timer, and the recording's file
/// name comes from it, so a mark still in place when a recording starts ends up in the file name
/// ("Forensic Files S13E33 ↪ Deadly Rebellion.ts"). Episode History fixes timers of marked entries
/// on its next sweep, but an airing already on the air starts recording the moment it is scheduled.
///
/// The symbols come from Episode History's settings file (current, default and every symbol used
/// before), so there's no code dependency; without that plugin this does nothing.
/// </summary>
public sealed class GuideMarks
{
    private static readonly string[] DefaultSymbols = { "✓", "●", "○", "🟧", "↪" };
    private static readonly string[] Labels = { "Watched", "In library", "Recorded before", "Not recording", "Recording another airing" };

    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<GuideMarks> _logger;

    public GuideMarks(IApplicationPaths paths, ILibraryManager libraryManager, ILogger<GuideMarks> logger)
    {
        _paths = paths;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Removes the marks from this programme's episode title (saved at once). True if it had any.</summary>
    public async Task<bool> UnmarkAsync(Guid programId, CancellationToken ct)
    {
        if (_libraryManager.GetItemById(programId) is not LiveTvProgram program || string.IsNullOrEmpty(program.EpisodeTitle))
        {
            return false;
        }

        var clean = Strip(program.EpisodeTitle, Symbols());
        if (string.Equals(clean, program.EpisodeTitle, StringComparison.Ordinal))
        {
            return false;
        }

        _logger.LogInformation("Removing guide mark before scheduling: {Old} -> {New}", program.EpisodeTitle, clean ?? "(none)");
        program.EpisodeTitle = clean;
        await program.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The title without leading mark symbols; null if only a mark label was left.</summary>
    public static string? Strip(string title, IReadOnlyList<string> symbols)
    {
        var rest = title;
        bool any;
        do
        {
            any = false;
            foreach (var s in symbols)
            {
                if (rest.StartsWith(s, StringComparison.Ordinal))
                {
                    rest = rest[s.Length..];
                    any = true;
                }
            }
        }
        while (any && rest.Length > 0);

        if (ReferenceEquals(rest, title))
        {
            return title;
        }

        rest = rest.TrimStart();
        return rest.Length == 0 || Labels.Contains(rest, StringComparer.Ordinal) ? null : rest;
    }

    private List<string> Symbols()
    {
        var symbols = new List<string>(DefaultSymbols);
        try
        {
            var file = Path.Combine(_paths.PluginConfigurationsPath, "Jellyfin.Plugin.EpisodeHistory.xml");
            if (File.Exists(file))
            {
                var root = XDocument.Load(file).Root;
                foreach (var name in new[] { "WatchedSymbol", "InLibrarySymbol", "RecordedBeforeSymbol", "SkippedSymbol", "OtherAiringSymbol" })
                {
                    if (root?.Element(name)?.Value is { Length: > 0 } v)
                    {
                        symbols.Add(v.Trim());
                    }
                }

                symbols.AddRange(root?.Element("UsedSymbols")?.Elements().Select(e => e.Value.Trim()).Where(v => v.Length > 0) ?? Enumerable.Empty<string>());
            }
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Can't read Episode History's settings");
        }

        // Longest first, so a multi-character symbol wins over one it starts with.
        return symbols.Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToList();
    }
}
