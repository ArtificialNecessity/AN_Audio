// Spec 71 D3 — a deliberately small C preprocessor: evaluates #if/#ifdef/#ifndef/#elif/#else/#endif against an AUTHORED define set,
// records #define/#undef the headers introduce, tracks #pragma pack, and blanks everything that is not live declaration text while
// preserving every character offset (so line numbers in the report point at the real header). No macro expansion in declaration text,
// no #include following here (the wanted list orders headers explicitly; includes are reported). A bare identifier in an #if that
// nobody declared is an ERROR, not a silent 0 — the whole point of D3.
using System.Text;
using System.Text.RegularExpressions;

namespace AN.Audio.BindingsCompiler;

public sealed class CHeader_Preprocessor
{
    /// <summary>Mutable define table shared across the headers of one surface (asiosys.h defines what asio.h tests).</summary>
    public Dictionary<string, string> Defines { get; }
    public Dictionary<string, string> DefinesFromHeaders { get; } = [];
    public List<string> Errors { get; } = [];
    public List<CHeader_Skip> Skips { get; } = [];

    public CHeader_Preprocessor(Dictionary<string, string> authoredDefines)
    {
        Defines = new Dictionary<string, string>(authoredDefines, StringComparer.Ordinal);
    }

    /// <summary>Result of one header: masked text (same length as source) + pack transitions by offset.</summary>
    public sealed record Result(string MaskedText, List<(int Offset, int Pack)> PackTransitions);

    public Result Process(string headerPath, string source)
    {
        // C translation phases: comments become whitespace (phase 3) BEFORE directives are processed (phase 4) — asio.h has
        // `#if kCanTimeCode` inside a /* example */ block that must never be evaluated.
        source = BlankComments(headerPath, source);
        char[] chars = source.ToCharArray();
        var packTransitions = new List<(int, int)>();
        var packStack = new Stack<int>();
        int currentPack = 0;

        // Conditional stack: each frame = (parentLive, thisBranchLive, anyBranchTakenSoFar)
        var frames = new Stack<(bool ParentLive, bool Live, bool Taken)>();
        bool live = true;

        int i = 0;
        while (i < source.Length)
        {
            int lineStart = i;
            int lineEnd = source.IndexOf('\n', i);
            if (lineEnd < 0) lineEnd = source.Length;
            // Directive? (only whitespace before '#')
            int p = lineStart;
            while (p < lineEnd && (source[p] == ' ' || source[p] == '\t')) p++;
            bool isDirective = p < lineEnd && source[p] == '#';
            int directiveEnd = lineEnd;
            if (isDirective)
            {
                // Join continuation lines
                while (directiveEnd < source.Length && source.AsSpan(lineStart, directiveEnd - lineStart).TrimEnd('\r').EndsWith("\\"))
                {
                    int next = source.IndexOf('\n', directiveEnd + 1);
                    directiveEnd = next < 0 ? source.Length : next;
                }
                string text = source.Substring(p + 1, directiveEnd - p - 1).Replace("\\\r\n", " ").Replace("\\\n", " ").Trim();
                int line = LineOf(source, lineStart);
                HandleDirective(headerPath, line, text, ref live, frames, packStack, ref currentPack, packTransitions, directiveEnd);
                Blank(chars, lineStart, directiveEnd);
                i = directiveEnd < source.Length ? directiveEnd + 1 : directiveEnd;
                continue;
            }
            if (!live) Blank(chars, lineStart, lineEnd);
            i = lineEnd < source.Length ? lineEnd + 1 : lineEnd;
        }
        if (frames.Count != 0) Errors.Add($"{headerPath}: {frames.Count} unterminated #if block(s)");
        return new Result(new string(chars), packTransitions);
    }

    private void HandleDirective(string header, int line, string text, ref bool live, Stack<(bool, bool, bool)> frames, Stack<int> packStack, ref int currentPack, List<(int, int)> packTransitions, int offset)
    {
        var m = Regex.Match(text, @"^(\w+)\s*(.*)$", RegexOptions.Singleline);
        if (!m.Success) return;
        string keyword = m.Groups[1].Value, rest = m.Groups[2].Value.Trim();
        switch (keyword)
        {
            case "if":
            case "ifdef":
            case "ifndef":
            {
                bool parentLive = live;
                bool cond = false;
                if (parentLive)
                {
                    cond = keyword switch
                    {
                        "ifdef" => Defines.ContainsKey(rest),
                        "ifndef" => !Defines.ContainsKey(rest),
                        _ => Evaluate(header, line, rest),
                    };
                }
                frames.Push((parentLive, parentLive && cond, cond));
                live = parentLive && cond;
                break;
            }
            case "elif":
            {
                if (frames.Count == 0) { Errors.Add($"{header}:{line}: #elif without #if"); return; }
                var (parentLive, _, taken) = frames.Pop();
                bool cond = parentLive && !taken && Evaluate(header, line, rest);
                frames.Push((parentLive, parentLive && cond, taken || cond));
                live = parentLive && cond;
                break;
            }
            case "else":
            {
                if (frames.Count == 0) { Errors.Add($"{header}:{line}: #else without #if"); return; }
                var (parentLive, _, taken) = frames.Pop();
                frames.Push((parentLive, parentLive && !taken, true));
                live = parentLive && !taken;
                break;
            }
            case "endif":
            {
                if (frames.Count == 0) { Errors.Add($"{header}:{line}: #endif without #if"); return; }
                var (parentLive, _, _) = frames.Pop();
                live = parentLive;
                break;
            }
            case "define" when live:
            {
                var d = Regex.Match(rest, @"^([A-Za-z_]\w*)(\([^)]*\))?\s*(.*)$", RegexOptions.Singleline);
                if (!d.Success) { Errors.Add($"{header}:{line}: malformed #define"); return; }
                if (d.Groups[2].Success) { Skips.Add(new(header, line, "function-like macro ignored", Preview(text))); return; }
                string name = d.Groups[1].Value, value = d.Groups[3].Value.Trim();
                if (Defines.TryGetValue(name, out string? existing) && existing != value && DefinesFromHeaders.ContainsKey(name) is false)
                    Errors.Add($"{header}:{line}: header defines {name} = '{value}' but the wanted list authored '{existing}' — make them agree");
                Defines[name] = value;
                DefinesFromHeaders[name] = value;
                break;
            }
            case "undef" when live:
                Defines.Remove(rest);
                DefinesFromHeaders.Remove(rest);
                break;
            case "include" when live:
                Skips.Add(new(header, line, "#include not followed (headers are ordered by the wanted list)", Preview(text)));
                break;
            case "pragma" when live:
            {
                var pk = Regex.Match(rest, @"^pack\s*\(\s*(.*?)\s*\)$");
                if (pk.Success)
                {
                    string[] args = pk.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (args.Length == 0) currentPack = 0;
                    else if (args[0] == "push") { packStack.Push(currentPack); if (args.Length > 1) currentPack = int.Parse(args[1]); }
                    else if (args[0] == "pop") currentPack = packStack.Count > 0 ? packStack.Pop() : 0;
                    else currentPack = int.Parse(args[0]);
                    packTransitions.Add((offset, currentPack));
                }
                else Skips.Add(new(header, line, "#pragma ignored", Preview(text)));
                break;
            }
            case "define" or "undef" or "include" or "pragma" or "error" or "warning":
                break; // inactive branch
            default:
                if (live) Skips.Add(new(header, line, $"unknown directive #{keyword}", Preview(text)));
                break;
        }
    }

    // ─── #if expression evaluation ───────────────────────────────────────────────────────────────────────────────────────

    public bool Evaluate(string header, int line, string expression)
    {
        var tokens = Regex.Matches(expression, @"\s*(&&|\|\||==|!=|<=|>=|[!<>()+\-*/]|0[xX][0-9A-Fa-f]+[uUlL]*|\d+[uUlL]*|[A-Za-z_]\w*)").Select(x => x.Groups[1].Value).ToList();
        int pos = 0;
        long value = ParseOr();
        if (pos != tokens.Count) Errors.Add($"{header}:{line}: cannot parse #if expression '{expression}'");
        return value != 0;

        long ParseOr() { long v = ParseAnd(); while (Peek() == "||") { pos++; long r = ParseAnd(); v = (v != 0 || r != 0) ? 1 : 0; } return v; }
        long ParseAnd() { long v = ParseCmp(); while (Peek() == "&&") { pos++; long r = ParseCmp(); v = (v != 0 && r != 0) ? 1 : 0; } return v; }
        long ParseCmp()
        {
            long v = ParseAdd();
            while (Peek() is "==" or "!=" or "<" or ">" or "<=" or ">=")
            {
                string op = tokens[pos++]; long r = ParseAdd();
                v = op switch { "==" => v == r ? 1 : 0, "!=" => v != r ? 1 : 0, "<" => v < r ? 1 : 0, ">" => v > r ? 1 : 0, "<=" => v <= r ? 1 : 0, _ => v >= r ? 1 : 0 };
            }
            return v;
        }
        long ParseAdd() { long v = ParseMul(); while (Peek() is "+" or "-") { string op = tokens[pos++]; long r = ParseMul(); v = op == "+" ? v + r : v - r; } return v; }
        long ParseMul() { long v = ParseUnary(); while (Peek() is "*" or "/") { string op = tokens[pos++]; long r = ParseUnary(); v = op == "*" ? v * r : (r == 0 ? 0 : v / r); } return v; }
        long ParseUnary()
        {
            string? t = Peek();
            if (t == "!") { pos++; return ParseUnary() == 0 ? 1 : 0; }
            if (t == "-") { pos++; return -ParseUnary(); }
            if (t == "(") { pos++; long v = ParseOr(); if (Peek() == ")") pos++; else Errors.Add($"{header}:{line}: missing ')' in '{expression}'"); return v; }
            if (t == "defined")
            {
                pos++;
                bool paren = Peek() == "("; if (paren) pos++;
                string name = pos < tokens.Count ? tokens[pos++] : "";
                if (paren && Peek() == ")") pos++;
                return Defines.ContainsKey(name) ? 1 : 0;
            }
            if (t is null) { Errors.Add($"{header}:{line}: unexpected end of #if expression '{expression}'"); return 0; }
            pos++;
            if (char.IsDigit(t[0])) return ParseNumber(t);
            if (char.IsLetter(t[0]) || t[0] == '_')
            {
                if (!Defines.TryGetValue(t, out string? v))
                {
                    Errors.Add($"{header}:{line}: #if references undeclared symbol '{t}' — add it to the wanted list's defines (D3: no guessing)");
                    return 0;
                }
                if (v.Length == 0) return 1; // defined with no value: C says the token disappears; the common intent in guards is 'true'
                if (Regex.IsMatch(v, @"^(0[xX][0-9A-Fa-f]+|\d+)[uUlL]*$")) return ParseNumber(v);
                if (Regex.IsMatch(v, @"^[A-Za-z_]\w*$") && Defines.TryGetValue(v, out string? v2) && Regex.IsMatch(v2, @"^\d+$")) return ParseNumber(v2);
                Errors.Add($"{header}:{line}: define '{t}' = '{v}' is not an integer; cannot evaluate '{expression}'");
                return 0;
            }
            Errors.Add($"{header}:{line}: unexpected token '{t}' in '{expression}'");
            return 0;
        }
        string? Peek() => pos < tokens.Count ? tokens[pos] : null;
    }

    private static long ParseNumber(string t)
    {
        t = t.TrimEnd('u', 'U', 'l', 'L');
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(t[2..], 16) : long.Parse(t);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void Blank(char[] chars, int start, int end)
    {
        for (int j = start; j < end && j < chars.Length; j++) if (chars[j] is not ('\r' or '\n')) chars[j] = ' ';
    }

    /// <summary>Blank // and /* */ comments in already-directive-free text, preserving offsets and newlines; string literals are respected.</summary>
    public string BlankComments(string header, string text)
    {
        char[] chars = text.ToCharArray();
        for (int i = 0; i < text.Length;)
        {
            if (text[i] is '"' or '\'')
            {
                char q = text[i++];
                while (i < text.Length && text[i] != q && text[i] != '\n') { if (text[i] == '\\') i++; i++; }
                i++;
            }
            else if (text.AsSpan(i).StartsWith("//"))
            {
                int start = i; while (i < text.Length && text[i] != '\n') i++;
                Blank(chars, start, i);
            }
            else if (text.AsSpan(i).StartsWith("/*"))
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) { Errors.Add($"{header}:{LineOf(text, i)}: unterminated comment"); end = text.Length - 2; }
                Blank(chars, i, end + 2); i = end + 2;
            }
            else i++;
        }
        return new string(chars);
    }

    private static string StripComments(string directiveText)
    {
        int c = directiveText.IndexOf("//", StringComparison.Ordinal);
        if (c >= 0) directiveText = directiveText[..c];
        return Regex.Replace(directiveText, @"/\*.*?\*/", " ", RegexOptions.Singleline);
    }

    public static int LineOf(string text, int offset) => text.AsSpan(0, Math.Min(offset, text.Length)).Count('\n') + 1;

    public static string Preview(string text)
    {
        text = Regex.Replace(text.Trim(), @"\s+", " ");
        return text.Length <= 160 ? text : text[..160] + "…";
    }
}