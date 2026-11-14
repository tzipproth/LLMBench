using System.Text;

namespace LLMBench.Core.Auto;

/// <summary>Splits a command line like a POSIX shell does (without variables, globbing or redirection).</summary>
public static class CommandLine
{
    /// <summary>
    /// Whitespace separates arguments; '…' is taken literally; "…" allows \" and \\; a backslash outside quotes
    /// escapes the next character, and backslash + line break (line continuation) is removed.
    /// </summary>
    public static List<string> Split(string text)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        bool inArg = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == '\n' || text[i + 1] == '\r'))
            {
                i++;
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (inArg) { result.Add(cur.ToString()); cur.Clear(); inArg = false; }
                continue;
            }
            inArg = true;
            if (c == '\'')
            {
                int end = text.IndexOf('\'', i + 1);
                if (end < 0) throw new FormatException("unclosed single quote.");
                cur.Append(text, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '"')
            {
                i++;
                for (; ; i++)
                {
                    if (i >= text.Length) throw new FormatException("unclosed double quote.");
                    if (text[i] == '"') break;
                    if (text[i] == '\\' && i + 1 < text.Length && (text[i + 1] is '"' or '\\' or '$' or '`')) i++;
                    cur.Append(text[i]);
                }
            }
            else if (c == '\\' && i + 1 < text.Length) cur.Append(text[++i]);
            else cur.Append(c);
        }
        if (inArg) result.Add(cur.ToString());
        return result;
    }
}
