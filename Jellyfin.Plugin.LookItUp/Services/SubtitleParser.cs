using System.Text.RegularExpressions;
using Jellyfin.Plugin.LookItUp.Models;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Parses common subtitle formats into timed cues.
/// </summary>
public interface ISubtitleParser
{
    /// <summary>
    /// Parses subtitle file contents into cues.
    /// </summary>
    /// <param name="content">Raw subtitle file text.</param>
    /// <param name="fileName">Original file name, used to detect format.</param>
    /// <returns>Parsed cues.</returns>
    IReadOnlyList<SubtitleCue> Parse(string content, string fileName);
}

/// <summary>
/// Parses SRT and WebVTT subtitle files.
/// </summary>
public partial class SubtitleParser : ISubtitleParser
{
    /// <inheritdoc />
    public IReadOnlyList<SubtitleCue> Parse(string content, string fileName)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".vtt" => ParseVtt(content),
            _ => ParseSrt(content)
        };
    }

    private static List<SubtitleCue> ParseSrt(string content)
    {
        var cues = new List<SubtitleCue>();
        var blocks = SrtBlockRegex().Split(content.Replace("\r\n", "\n"));

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2)
            {
                continue;
            }

            var timingLineIndex = lines[0].Contains("-->", StringComparison.Ordinal) ? 0 : 1;
            if (timingLineIndex >= lines.Length || !lines[timingLineIndex].Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryParseTiming(lines[timingLineIndex], out var startMs, out var endMs))
            {
                continue;
            }

            var textLines = lines.Skip(timingLineIndex + 1);
            var text = CleanSubtitleText(string.Join(' ', textLines));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            cues.Add(new SubtitleCue
            {
                StartMs = startMs,
                EndMs = endMs,
                Text = text
            });
        }

        return cues;
    }

    private static List<SubtitleCue> ParseVtt(string content)
    {
        var cues = new List<SubtitleCue>();
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryParseTiming(lines[i], out var startMs, out var endMs))
            {
                continue;
            }

            var textLines = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (string.IsNullOrWhiteSpace(lines[j]) || lines[j].Contains("-->", StringComparison.Ordinal))
                {
                    break;
                }

                textLines.Add(lines[j]);
            }

            var text = CleanSubtitleText(string.Join(' ', textLines));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            cues.Add(new SubtitleCue
            {
                StartMs = startMs,
                EndMs = endMs,
                Text = text
            });
        }

        return cues;
    }

    private static bool TryParseTiming(string line, out long startMs, out long endMs)
    {
        startMs = 0;
        endMs = 0;
        var match = TimingRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        startMs = ParseTimestamp(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value);
        endMs = ParseTimestamp(match.Groups[5].Value, match.Groups[6].Value, match.Groups[7].Value, match.Groups[8].Value);
        return true;
    }

    private static long ParseTimestamp(string hours, string minutes, string seconds, string fraction)
    {
        var ms = fraction.PadRight(3, '0')[..3];
        return (long.Parse(hours) * 3600000)
               + (long.Parse(minutes) * 60000)
               + (long.Parse(seconds) * 1000)
               + long.Parse(ms);
    }

    private static string CleanSubtitleText(string text)
    {
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = AssOverrideRegex().Replace(text, string.Empty);
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"\n\s*\n", RegexOptions.CultureInvariant)]
    private static partial Regex SrtBlockRegex();

    [GeneratedRegex(
        @"(\d{1,2}):(\d{2}):(\d{2})[.,](\d{1,3})\s*-->\s*(\d{1,2}):(\d{2}):(\d{2})[.,](\d{1,3})",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimingRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\{[^}]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex AssOverrideRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
