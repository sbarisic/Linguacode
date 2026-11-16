using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Linguacode.Core;

public static class TranscriptText
{
    // Whisper tokens are byte fragments, not necessarily complete UTF-8 characters.
    // Their separately decoded strings can contain U+FFFD even when the complete
    // segment is intact. Recover text only from that segment; retain word timings.
    public static bool RestoreWords(string text, JsonArray words)
    {
        var fragments = words.Select(w => w?["text"]?.GetValue<string>() ?? "").ToArray();
        var joined = string.Concat(fragments);
        if (joined.Trim() == text.Trim()) return true;
        if (words.Count == 0 || !joined.Contains('\uFFFD') || text.Contains('\uFFFD')) return false;

        var pattern = new StringBuilder("\\A");
        for (var index = 0; index < fragments.Length; index++)
        {
            pattern.Append("(?<w").Append(index).Append('>');
            var fragment = fragments[index];
            for (var i = 0; i < fragment.Length;)
            {
                if (fragment[i] != '\uFFFD')
                {
                    var start = i++;
                    while (i < fragment.Length && fragment[i] != '\uFFFD') i++;
                    pattern.Append(Regex.Escape(fragment[start..i]));
                }
                else
                {
                    var start = i++;
                    while (i < fragment.Length && fragment[i] == '\uFFFD') i++;
                    // Split UTF-8 sequences can only have damaged non-ASCII text.
                    pattern.Append("[^\\x00-\\x7F]{1,").Append(i - start).Append('}');
                }
            }
            pattern.Append(')');
        }
        pattern.Append("\\z");
        var leading = joined[..(joined.Length - joined.TrimStart().Length)];
        var trailing = joined[joined.TrimEnd().Length..];
        var source = leading + text.Trim() + trailing;
        Match match;
        try { match = Regex.Match(source, pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
        catch (RegexMatchTimeoutException) { return false; }
        if (!match.Success) return false;
        for (var i = 0; i < words.Count; i++) words[i]!["text"] = match.Groups["w" + i].Value;
        return true;
    }
}
