using System.Globalization;
using System.Text;

namespace MainframeEngine.Editor;

/// <summary>Helpers for generating RML: escaping user text and formatting dp lengths.</summary>
public static class RmlText
{
    /// <summary>Escapes text for RML content and attribute values (<c>&amp; &lt; &gt; &quot; '</c>, and <c>{</c> so data bindings never see user text).</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var needs = false;
        foreach (var c in text)
            if (c is '&' or '<' or '>' or '"' or '\'' or '{' or '}')
            {
                needs = true;
                break;
            }

        if (!needs)
            return text;
        var builder = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&#39;"); break;
                case '{': builder.Append("&#123;"); break;
                case '}': builder.Append("&#125;"); break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }

    /// <summary>A dp length: <c>12.5dp</c>.</summary>
    public static string Dp(float value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "dp";
}
