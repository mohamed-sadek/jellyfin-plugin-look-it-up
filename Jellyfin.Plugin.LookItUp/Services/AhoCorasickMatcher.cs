using System.Text;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Aho-Corasick scanner over lowercase Latin/Unicode text with word-boundary matches.
/// </summary>
internal sealed class AhoCorasickMatcher
{
    private readonly Node _root = new();
    private readonly string[] _phrases;
    private bool _built;

    public AhoCorasickMatcher(IEnumerable<string> phrases)
    {
        _phrases = phrases.ToArray();
        for (var i = 0; i < _phrases.Length; i++)
        {
            Insert(_phrases[i], i);
        }
    }

    /// <summary>
    /// Returns pattern index + start offset for each word-bounded match.
    /// </summary>
    public IReadOnlyList<(int PhraseIndex, int Start, int Length)> Find(string text)
    {
        EnsureBuilt();
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var hits = new List<(int PhraseIndex, int Start, int Length)>();
        var node = _root;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            while (node != _root && !node.Next.ContainsKey(ch))
            {
                node = node.Fail ?? _root;
            }

            if (node.Next.TryGetValue(ch, out var next))
            {
                node = next;
            }

            var output = node;
            while (output is not null)
            {
                foreach (var index in output.Outputs)
                {
                    var length = _phrases[index].Length;
                    var start = i - length + 1;
                    if (start >= 0 && IsWordBounded(text, start, length))
                    {
                        hits.Add((index, start, length));
                    }
                }

                output = output.Fail;
                if (output == _root)
                {
                    break;
                }
            }
        }

        return SelectLongestNonOverlapping(hits);
    }

    private static bool IsWordBounded(string text, int start, int length)
    {
        if (start > 0 && IsWordChar(text[start - 1]))
        {
            return false;
        }

        var end = start + length;
        if (end < text.Length)
        {
            var next = text[end];
            if (IsWordChar(next))
            {
                return false;
            }

            // "Oskar Schindler's" still matches Schindler.
            if (next is '\'' or '’' && end + 1 < text.Length && text[end + 1] is 's' or 'S')
            {
                return true;
            }
        }

        return true;
    }

    private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch);

    private static IReadOnlyList<(int PhraseIndex, int Start, int Length)> SelectLongestNonOverlapping(
        List<(int PhraseIndex, int Start, int Length)> hits)
    {
        if (hits.Count == 0)
        {
            return hits;
        }

        var ordered = hits
            .OrderBy(h => h.Start)
            .ThenByDescending(h => h.Length)
            .ToList();
        var chosen = new List<(int PhraseIndex, int Start, int Length)>();
        var occupiedUntil = -1;
        foreach (var hit in ordered)
        {
            if (hit.Start < occupiedUntil)
            {
                continue;
            }

            chosen.Add(hit);
            occupiedUntil = hit.Start + hit.Length;
        }

        return chosen;
    }

    private void Insert(string phrase, int index)
    {
        var node = _root;
        foreach (var ch in phrase)
        {
            if (!node.Next.TryGetValue(ch, out var child))
            {
                child = new Node();
                node.Next[ch] = child;
            }

            node = child;
        }

        node.Outputs.Add(index);
    }

    private void EnsureBuilt()
    {
        if (_built)
        {
            return;
        }

        var queue = new Queue<Node>();
        foreach (var child in _root.Next.Values)
        {
            child.Fail = _root;
            queue.Enqueue(child);
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var (ch, child) in node.Next)
            {
                var fail = node.Fail;
                while (fail is not null && fail != _root && !fail.Next.ContainsKey(ch))
                {
                    fail = fail.Fail;
                }

                child.Fail = fail is not null && fail.Next.TryGetValue(ch, out var failChild)
                    ? failChild
                    : _root;
                if (child.Fail.Outputs.Count > 0)
                {
                    child.Outputs.AddRange(child.Fail.Outputs);
                }

                queue.Enqueue(child);
            }
        }

        _built = true;
    }

    private sealed class Node
    {
        public Dictionary<char, Node> Next { get; } = new();

        public Node? Fail { get; set; }

        public List<int> Outputs { get; } = [];
    }

    /// <summary>Lowercases for matching without allocating per cue more than once.</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}
