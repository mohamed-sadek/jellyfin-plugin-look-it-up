using Jellyfin.Plugin.LookItUp.Models;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Builds a short adjacent-cue window so Wikipedia search can disambiguate names.
/// </summary>
internal static class CueSearchContext
{
    public static void Attach(IReadOnlyList<SubtitleCue> cues, IEnumerable<NameCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            candidate.SearchContext = Build(cues, candidate);
        }
    }

    public static string Build(IReadOnlyList<SubtitleCue> cues, NameCandidate candidate)
    {
        if (cues.Count == 0)
        {
            return candidate.CueText;
        }

        var idx = -1;
        for (var i = 0; i < cues.Count; i++)
        {
            if (cues[i].StartMs != candidate.StartMs)
            {
                continue;
            }

            if (string.IsNullOrEmpty(candidate.CueText)
                || cues[i].Text.Contains(candidate.Term, StringComparison.OrdinalIgnoreCase)
                || Normalize(cues[i].Text) == Normalize(candidate.CueText))
            {
                idx = i;
                break;
            }

            if (idx < 0)
            {
                idx = i;
            }
        }

        if (idx < 0)
        {
            return candidate.CueText;
        }

        var parts = new List<string>();
        if (idx > 0)
        {
            parts.Add(cues[idx - 1].Text);
        }

        parts.Add(cues[idx].Text);
        return string.Join('\n', parts);
    }

    private static string Normalize(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
