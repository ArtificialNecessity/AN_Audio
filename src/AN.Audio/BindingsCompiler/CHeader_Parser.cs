// Spec 71 D4 — tokenizer + recursive-descent parser for the C subset our SDK headers use, plus the one C++ construct ASIO needs
// (`interface X : public IUnknown { virtual T m(args) = 0; }`). Chosen over a PEG grammar for the same reason the Wayland compiler
// chose XLinq: the input is small and schema-stable, and a hand parser is directly unit-testable. Unrecognised top-level text is
// SKIPPED AND REPORTED, never guessed. Operates on the preprocessor's masked text so offsets map to real header lines.
using System.Text.RegularExpressions;

namespace AN.Audio.BindingsCompiler;

public sealed class CHeader_Parser
{
    public sealed record Token(string Text, int Offset, char Kind); // Kind: 'i' ident, 'n' number, 'p' punct, 's' string

    public sealed class ParseResult
    {
        public List<CHeader_TypedefDecl> Typedefs { get; } = [];
        public List<CHeader_StructDecl> Structs { get; } = [];
        public List<CHeader_EnumDecl> Enums { get; } = [];
        public List<CHeader_ClassDecl> Classes { get; } = [];
        public List<CHeader_Skip> Skips { get; } = [];
        public List<string> Errors { get; } = [];
    }

    private readonly string _header;
    private readonly string _text;
    private readonly List<Token> _t;
    private readonly List<(int Offset, int Pack)> _packs;
    private readonly Dictionary<string, string> _defines;
    private readonly ParseResult _r = new();
    private int _p;

    public CHeader_Parser(string header, string maskedText, List<(int Offset, int Pack)> packTransitions, Dictionary<string, string> defines)
    {
        _header = header; _text = maskedText; _packs = packTransitions; _defines = defines;
        _t = Tokenize(maskedText);
    }

    public static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        var rx = new Regex(
            """\G(?:\s+|(?<i>[A-Za-z_]\w*)|(?<n>0[xX][0-9A-Fa-f]+[uUlL]*|\d+[uUlL]*)|(?<s>"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')|(?<p>::|<<|>>|->|==|!=|<=|>=|&&|\|\||[{}()\[\];,*&=:<>+\-/|!~^.?]))""",
            RegexOptions.Compiled);
        int i = 0;
        while (i < s.Length)
        {
            var m = rx.Match(s, i);
            if (!m.Success || m.Length == 0) { list.Add(new(s[i].ToString(), i, 'p')); i++; continue; }
            if (m.Groups["i"].Success) list.Add(new(m.Value.Trim(), m.Groups["i"].Index, 'i'));
            else if (m.Groups["n"].Success) list.Add(new(m.Groups["n"].Value, m.Groups["n"].Index, 'n'));
            else if (m.Groups["s"].Success) list.Add(new(m.Groups["s"].Value, m.Groups["s"].Index, 's'));
            else if (m.Groups["p"].Success) list.Add(new(m.Groups["p"].Value, m.Groups["p"].Index, 'p'));
            i = m.Index + m.Length;
        }
        return list;
    }

    // ─── top level ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public ParseResult Parse()
    {
        while (_p < _t.Count)
        {
            int start = _p;
            try
            {
                if (!ParseTopLevel())
                {
                    // Unrecognised: skip a whole statement (balanced braces) and report it.
                    int end = SkipStatement();
                    _r.Skips.Add(new(_header, LineAt(start), "unrecognised top-level declaration", Preview(start, end)));
                }
            }
            catch (ParseError e)
            {
                _p = Math.Max(_p, start);
                int end = SkipStatement();
                _r.Skips.Add(new(_header, LineAt(start), "unsupported declaration: " + e.Message, Preview(start, end)));
            }
        }
        return _r;
    }

    private bool ParseTopLevel()
    {
        var t = Cur;
        if (t.Kind == 'p' && t.Text == ";") { _p++; return true; }
        if (t.Kind != 'i') return false;
        switch (t.Text)
        {
            case "typedef": ParseTypedef(); return true;
            case "struct" when PeekIs(1, 'i') && PeekIs(2, "{"): { _p++; string tag = Next().Text; ParseStructBody(tag, t.Offset); Expect(";"); return true; }
            case "enum" when PeekIs(1, "{") || (PeekIs(1, 'i') && PeekIs(2, "{")): { _p++; string? tag = Cur.Text == "{" ? null : Next().Text; ParseEnumBody(tag, t.Offset); Expect(";"); return true; }
            case "interface" or "class" when PeekIs(1, 'i') && PeekIs(2, ":"): ParseClass(); return true;
        }
        // Function prototype `T name(args);` — the C-API layer we do not bind. Recognise so the report says what it was.
        int save = _p;
        try
        {
            ParseType(); 
            if (Cur.Kind == 'i' && PeekIs(1, "(")) { int end = SkipStatement(); _r.Skips.Add(new(_header, LineAt(save), "function prototype (C-API layer, not bound)", Preview(save, end))); return true; }
        }
        catch (ParseError) { }
        _p = save;
        return false;
    }

    private void ParseTypedef()
    {
        int start = _p; Expect("typedef");
        if (Cur.Text == "struct" || Cur.Text == "enum")
        {
            bool isEnum = Cur.Text == "enum"; _p++;
            string? tag = Cur.Kind == 'i' ? Next().Text : null;
            if (Cur.Text == "{")
            {
                if (isEnum) { var e = ParseEnumBody(tag, _t[start].Offset); string name = ExpectIdent(); e.Name ??= name; if (tag is not null && tag != name) _r.Typedefs.Add(new(name, new(tag, 0), _header, LineAt(start))); }
                else { var s = ParseStructBody(tag ?? "", _t[start].Offset); string name = ExpectIdent(); if (s.Name.Length == 0) s.Name = name; else if (s.Name != name) _r.Typedefs.Add(new(name, new(s.Name, 0), _header, LineAt(start))); }
                Expect(";"); return;
            }
            // `typedef struct Tag Name;` forward/alias
            string alias = ExpectIdent(); Expect(";");
            _r.Typedefs.Add(new(alias, new(tag ?? throw new ParseError("typedef struct without tag"), 0), _header, LineAt(start)));
            return;
        }
        if (Cur.Text == "interface") throw new ParseError("forward `typedef interface` (not needed)");
        var type = ParseType();
        string tn = ExpectIdent();
        if (Cur.Text == "(") throw new ParseError("function typedef");
        Expect(";");
        _r.Typedefs.Add(new(tn, type, _header, LineAt(start)));
    }

    private CHeader_StructDecl ParseStructBody(string tag, int declOffset)
    {
        int line = CHeader_Preprocessor.LineOf(_text, declOffset); // the `typedef`/`struct` keyword's line, not the `{`
        Expect("{");
        var s = new CHeader_StructDecl { Name = tag, Pack = PackAt(declOffset), Header = _header, Line = line };
        while (Cur.Text != "}")
        {
            var type = ParseType();
            // function pointer: ret (*name)(params)
            if (Cur.Text == "(" && PeekIs(1, "*"))
            {
                _p += 2; string fname = ExpectIdent(); Expect(")");
                var ps = ParseParams();
                Expect(";");
                s.Fields.Add(new CHeader_FieldDecl { Name = fname, FunctionPointer = new(type, ps) });
                continue;
            }
            do
            {
                if (Cur.Text == ",") _p++;
                int extraPtr = 0; while (Cur.Text == "*") { extraPtr++; _p++; }
                string fname = ExpectIdent();
                int arr = 0;
                if (Cur.Text == "[") { _p++; arr = (int)EvalConst(ReadUntil("]")); Expect("]"); }
                s.Fields.Add(new CHeader_FieldDecl { Name = fname, Type = extraPtr == 0 ? type : type with { PointerDepth = type.PointerDepth + extraPtr }, ArrayLength = arr });
            } while (Cur.Text == ",");
            Expect(";");
        }
        Expect("}");
        _r.Structs.Add(s);
        return s;
    }

    private CHeader_EnumDecl ParseEnumBody(string? tag, int declOffset)
    {
        var e = new CHeader_EnumDecl { Name = tag, Header = _header, Line = CHeader_Preprocessor.LineOf(_text, declOffset) };
        Expect("{");
        long next = 0;
        var local = new Dictionary<string, long>(StringComparer.Ordinal);
        while (Cur.Text != "}")
        {
            string name = ExpectIdent();
            string? expr = null; long value = next;
            if (Cur.Text == "=")
            {
                _p++;
                var toks = ReadUntilAny(",", "}");
                expr = string.Join(" ", toks.Select(x => x.Text));
                value = EvalConst(toks, local);
            }
            e.Entries.Add(new(name, value, expr));
            local[name] = value; next = value + 1;
            if (Cur.Text == ",") _p++;
        }
        Expect("}");
        _r.Enums.Add(e);
        return e;
    }

    private void ParseClass()
    {
        int line = LineAt(_p);
        _p++; // interface|class
        string name = ExpectIdent(); Expect(":");
        if (Cur.Text is "public" or "private" or "protected") _p++;
        string baseName = ExpectIdent();
        Expect("{");
        var c = new CHeader_ClassDecl { Name = name, Base = baseName, Header = _header, Line = line };
        int slot = 0;
        while (Cur.Text != "}")
        {
            if (Cur.Text is "public" or "private" or "protected") { _p++; Expect(":"); continue; }
            if (Cur.Text != "virtual") { int st = _p; int end = SkipStatement(); _r.Skips.Add(new(_header, LineAt(st), "non-virtual member ignored", Preview(st, end))); continue; }
            _p++;
            var ret = ParseType();
            string mname = ExpectIdent();
            var ps = ParseParams();
            // `= 0` pure specifier (required), optional `const`
            if (Cur.Text == "const") _p++;
            if (Cur.Text == "=") { _p++; if (Next().Text != "0") throw new ParseError("expected pure virtual `= 0`"); }
            Expect(";");
            c.Methods.Add(new(mname, slot, ret, ps));
            slot++;
        }
        Expect("}"); Expect(";");
        _r.Classes.Add(c);
    }

    private List<CHeader_ParamDecl> ParseParams()
    {
        Expect("(");
        var ps = new List<CHeader_ParamDecl>();
        if (Cur.Text == "void" && PeekIs(1, ")")) { _p += 2; return ps; }
        while (Cur.Text != ")")
        {
            var type = ParseType();
            string pname = Cur.Kind == 'i' ? Next().Text : $"arg{ps.Count}";
            if (Cur.Text == "[") { _p++; ReadUntil("]"); Expect("]"); type = type with { PointerDepth = type.PointerDepth + 1 }; }
            ps.Add(new(pname, type));
            if (Cur.Text == ",") _p++;
        }
        Expect(")");
        return ps;
    }

    private static readonly HashSet<string> TypeWords = ["const", "volatile", "unsigned", "signed", "long", "short", "int", "char", "double", "float", "void", "struct", "enum", "__int64", "bool"];

    /// <summary>`const unsigned long`, `struct X`, `Name`, followed by `*`s. `const`/`struct` are dropped from the base text.</summary>
    private CHeader_TypeRef ParseType()
    {
        var parts = new List<string>();
        while (Cur.Kind == 'i' && (TypeWords.Contains(Cur.Text) || parts.Count == 0))
        {
            string w = Next().Text;
            if (w is "const" or "volatile" or "struct" or "enum") continue;
            parts.Add(w);
            if (!TypeWords.Contains(w)) break; // a typedef/tag name ends the specifier list
        }
        if (parts.Count == 0) throw new ParseError($"expected a type at '{Cur.Text}'");
        int ptr = 0;
        while (Cur.Text == "*" || Cur.Text == "const") { if (Next().Text == "*") ptr++; }
        return new(string.Join(' ', parts), ptr);
    }

    // ─── constant expressions (enum values, array lengths) ──────────────────────────────────────────────────────────────────────────────

    private long EvalConst(List<Token> toks, Dictionary<string, long>? local = null)
    {
        int i = 0;
        long v = Shift();
        if (i != toks.Count) throw new ParseError("cannot evaluate constant expression '" + string.Join(" ", toks.Select(x => x.Text)) + "'");
        return v;

        long Shift() { long a = Add(); while (i < toks.Count && toks[i].Text is "<<" or ">>") { string op = toks[i++].Text; long b = Add(); a = op == "<<" ? a << (int)b : a >> (int)b; } return a; }
        long Add() { long a = Mul(); while (i < toks.Count && toks[i].Text is "+" or "-" or "|") { string op = toks[i++].Text; long b = Mul(); a = op == "+" ? a + b : op == "-" ? a - b : a | b; } return a; }
        long Mul() { long a = Unary(); while (i < toks.Count && toks[i].Text is "*" or "/") { string op = toks[i++].Text; long b = Unary(); a = op == "*" ? a * b : a / b; } return a; }
        long Unary()
        {
            if (i >= toks.Count) throw new ParseError("unexpected end of constant expression");
            var t = toks[i++];
            if (t.Text == "-") return -Unary();
            if (t.Text == "~") return ~Unary();
            if (t.Text == "(") { long v = Shift(); if (i < toks.Count && toks[i].Text == ")") i++; return v; }
            if (t.Kind == 'n') return ParseNumber(t.Text);
            if (t.Kind == 's' && t.Text.StartsWith('\'')) return t.Text.Length == 3 ? t.Text[1] : throw new ParseError("char literal");
            if (t.Kind == 'i')
            {
                if (local is not null && local.TryGetValue(t.Text, out long lv)) return lv;
                if (_defines.TryGetValue(t.Text, out string? dv) && Regex.IsMatch(dv, @"^(0[xX][0-9A-Fa-f]+|\d+)[uUlL]*$")) return ParseNumber(dv);
                throw new ParseError($"unknown identifier '{t.Text}' in constant expression");
            }
            throw new ParseError($"unexpected '{t.Text}' in constant expression");
        }
    }

    private static long ParseNumber(string t)
    {
        t = t.TrimEnd('u', 'U', 'l', 'L');
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(t[2..], 16) : long.Parse(t);
    }

    // ─── token helpers ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class ParseError(string message) : Exception(message);
    private static readonly Token Eof = new("", int.MaxValue, 'e');
    private Token Cur => _p < _t.Count ? _t[_p] : Eof;
    private Token Next() { if (_p >= _t.Count) throw new ParseError("unexpected end of file"); return _t[_p++]; }
    private bool PeekIs(int ahead, string text) => _p + ahead < _t.Count && _t[_p + ahead].Text == text;
    private bool PeekIs(int ahead, char kind) => _p + ahead < _t.Count && _t[_p + ahead].Kind == kind;
    private void Expect(string text) { if (Cur.Text != text) throw new ParseError($"expected '{text}' but found '{Cur.Text}'"); _p++; }
    private string ExpectIdent() { if (Cur.Kind != 'i') throw new ParseError($"expected identifier but found '{Cur.Text}'"); return Next().Text; }
    private List<Token> ReadUntil(string stop) => ReadUntilAny(stop);
    private List<Token> ReadUntilAny(params string[] stops)
    {
        var list = new List<Token>(); int depth = 0;
        while (_p < _t.Count)
        {
            if (depth == 0 && stops.Contains(Cur.Text)) return list;
            if (Cur.Text == "(") depth++; else if (Cur.Text == ")") depth--;
            list.Add(Next());
        }
        throw new ParseError("unexpected end of file");
    }
    /// <summary>Advance past the next `;` at brace depth 0 (or a closing `};`). Returns the end token index.</summary>
    private int SkipStatement()
    {
        int depth = 0;
        while (_p < _t.Count)
        {
            var t = Next();
            if (t.Text == "{") depth++;
            else if (t.Text == "}") { depth--; if (depth <= 0 && Cur.Text == ";") { _p++; return _p; } if (depth < 0) depth = 0; }
            else if (t.Text == ";" && depth == 0) return _p;
        }
        return _p;
    }
    private int LineAt(int tokenIndex) => tokenIndex < _t.Count ? CHeader_Preprocessor.LineOf(_text, _t[tokenIndex].Offset) : CHeader_Preprocessor.LineOf(_text, _text.Length);
    private int PackAt(int offset) { int pack = 0; foreach (var (o, pk) in _packs) { if (o <= offset) pack = pk; else break; } return pack; }
    private string Preview(int startTok, int endTok)
    {
        if (startTok >= _t.Count) return "";
        int a = _t[startTok].Offset, b = endTok > startTok && endTok - 1 < _t.Count ? _t[endTok - 1].Offset + _t[endTok - 1].Text.Length : Math.Min(_text.Length, a + 160);
        return CHeader_Preprocessor.Preview(_text.Substring(a, Math.Max(0, b - a)));
    }
}