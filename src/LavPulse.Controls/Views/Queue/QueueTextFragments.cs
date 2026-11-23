using System.Globalization;
using System.Text;

namespace Laveyana.LavPulse.Controls;

/// <summary>
/// Short, unbreakable Unicode grapheme spans for cross-platform MAUI canvas text.
/// Every span uses one fixed baseline instead of native multi-line layout.
/// </summary>
internal static class QueueTextFragments
{
    internal const int MaxGraphemes = 8;

    public static string[] Split(string value)
    {
        if (string.IsNullOrEmpty(value)) return [];
        var parts = new List<string>();
        var segment = new StringBuilder();
        var enumerator = StringInfo.GetTextElementEnumerator(value.Replace('\r', ' ').Replace('\n', ' '));
        int count = 0;
        while (enumerator.MoveNext())
        {
            segment.Append(enumerator.GetTextElement());
            if (++count < MaxGraphemes) continue;
            parts.Add(segment.ToString());
            segment.Clear();
            count = 0;
        }
        if (segment.Length > 0) parts.Add(segment.ToString());
        return parts.ToArray();
    }
}
