// Spec 71 `extract`: wanted list + SDK headers on disk → CHeader_Declarations (committed boundary) + CHeader_ExtractionReport.
// Rules (same as FluidUI): every wanted item must be found EXACTLY ONCE; any error → declarations are NOT written, report always is.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YeetJson;

namespace AN.Audio.BindingsCompiler;

public static class CHeader_Extractor
{
    public static readonly string ToolVersion = "AN.Audio.BindingsCompiler 0.1 (spec 71)";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record ExtractionResult(CHeader_Declarations? Declarations, CHeader_ExtractionReport Report)
    {
        public bool Succeeded => Declarations is not null && Report.Errors.Count == 0;
    }

    /// <param name="sdkRootOverride">Optional: replaces every surface's SdkRoot (CI / other machines).</param>
    public static ExtractionResult Extract(string wantedListPath, string? sdkRootOverride)
    {
        string wantedText = File.ReadAllText(wantedListPath);
        var wanted = ParseHjson<CHeader_WantedList>(wantedText, wantedListPath);
        var report = new CHeader_ExtractionReport { ToolVersion = ToolVersion, WantedSha256 = Sha256Hex(wantedText) };
        var declarations = new CHeader_Declarations { Source = "explicit extraction from locally-installed SDK headers (spec 71); hashes in Extraction.report.json" };

        foreach (var surface in wanted.Surfaces)
        {
            string sdkRoot = sdkRootOverride ?? surface.SdkRoot;
            if (!Directory.Exists(sdkRoot)) { report.Errors.Add($"{surface.Name}: SDK root not found: {sdkRoot}"); continue; }
            var pre = new CHeader_Preprocessor(surface.Defines);
            var parsed = new CHeader_Parser.ParseResult();
            var decl = new CHeader_SurfaceDeclarations { Name = surface.Name, CsPrefix = surface.CsPrefix, StripPrefixes = surface.StripPrefixes, CsNamespace = surface.CsNamespace, Abis = surface.Abis, Defines = surface.Defines };

            foreach (string rel in surface.Headers)
            {
                string full = Path.Combine(sdkRoot, rel);
                if (!File.Exists(full)) { report.Errors.Add($"{surface.Name}: header not found: {rel}"); continue; }
                string source = File.ReadAllText(full);
                var record = new CHeader_HeaderRecord(rel.Replace('\\', '/'), Sha256Hex(source));
                report.Headers.Add(record); decl.Headers.Add(record);
                var result = pre.Process(rel, source);
                var p = new CHeader_Parser(rel, result.MaskedText, result.PackTransitions, pre.Defines).Parse();
                parsed.Typedefs.AddRange(p.Typedefs); parsed.Structs.AddRange(p.Structs); parsed.Enums.AddRange(p.Enums); parsed.Classes.AddRange(p.Classes);
                parsed.Skips.AddRange(p.Skips); parsed.Errors.AddRange(p.Errors);
            }
            report.Errors.AddRange(pre.Errors.Select(e => surface.Name + ": " + e));
            report.Errors.AddRange(parsed.Errors.Select(e => surface.Name + ": " + e));
            report.Skipped.AddRange(pre.Skips); report.Skipped.AddRange(parsed.Skips);
            foreach (var kv in pre.DefinesFromHeaders) report.DefinesFromHeaders[kv.Key] = kv.Value;

            // Select wanted items, exactly once each.
            foreach (string name in surface.Typedefs)
            {
                var hits = parsed.Typedefs.Where(t => t.Name == name).ToList();
                if (hits.Count == 1) decl.Typedefs.Add(hits[0]); else report.Errors.Add($"{surface.Name}: typedef '{name}' found {hits.Count} times (need exactly 1)");
            }
            foreach (string name in surface.Structs)
            {
                var hits = parsed.Structs.Where(s => s.Name == name).ToList();
                if (hits.Count == 1) decl.Structs.Add(hits[0]); else report.Errors.Add($"{surface.Name}: struct '{name}' found {hits.Count} times (need exactly 1)");
            }
            foreach (var we in surface.Enums)
            {
                List<CHeader_EnumDecl> hits;
                string key;
                if (we.Name is not null) { hits = parsed.Enums.Where(e => e.Name == we.Name).ToList(); key = "enum " + we.Name; }
                else if (we.FirstEntry is not null) { hits = parsed.Enums.Where(e => e.Name is null && e.Entries.Count > 0 && e.Entries[0].Name == we.FirstEntry).ToList(); key = "anonymous enum starting with " + we.FirstEntry; }
                else { report.Errors.Add($"{surface.Name}: enum '{we.CsName}' needs name or firstEntry"); continue; }
                if (hits.Count != 1) { report.Errors.Add($"{surface.Name}: {key} found {hits.Count} times (need exactly 1)"); continue; }
                var e = hits[0]; e.CsName = we.CsName; e.Flags = we.Flags; e.Typedef = we.Typedef;
                if (we.Typedef is not null && !parsed.Typedefs.Any(t => t.Name == we.Typedef)) report.Errors.Add($"{surface.Name}: enum {we.CsName}: typedef '{we.Typedef}' not declared in the headers");
                decl.Enums.Add(e);
            }
            foreach (var wc in surface.Classes)
            {
                var hits = parsed.Classes.Where(c => c.Name == wc.Name).ToList();
                if (hits.Count != 1) { report.Errors.Add($"{surface.Name}: class '{wc.Name}' found {hits.Count} times (need exactly 1)"); continue; }
                var c = hits[0];
                if (c.Base != wc.Base) report.Errors.Add($"{surface.Name}: class {wc.Name} derives from '{c.Base}' in the header, wanted list says '{wc.Base}'");
                c.CsName = wc.CsName; c.BaseSlots = wc.BaseSlots; c.CallConv = wc.CallConv;
                c.Methods = c.Methods.Select(m => m with { Slot = m.Slot + wc.BaseSlots }).ToList();
                decl.Classes.Add(c);
            }
            // Referenced-but-unwanted types are how the wanted list grows (FluidUI's "unwanted interface references").
            var known = new HashSet<string>(decl.Typedefs.Select(t => t.Name).Concat(decl.Structs.Select(s => s.Name)).Concat(decl.Enums.Where(e => e.Name is not null).Select(e => e.Name!)).Concat(decl.Classes.Select(c => c.Name)), StringComparer.Ordinal);
            foreach (var s in decl.Structs) foreach (var f in s.Fields) NoteReference(f.Type?.Base, $"struct {s.Name}.{f.Name}");
            foreach (var s in decl.Structs) foreach (var f in s.Fields) if (f.FunctionPointer is { } fp) { NoteReference(fp.ReturnType.Base, $"{s.Name}.{f.Name} return"); foreach (var pa in fp.Params) NoteReference(pa.Type.Base, $"{s.Name}.{f.Name}({pa.Name})"); }
            foreach (var c in decl.Classes) foreach (var m in c.Methods) { NoteReference(m.ReturnType.Base, $"{c.Name}::{m.Name} return"); foreach (var pa in m.Params) NoteReference(pa.Type.Base, $"{c.Name}::{m.Name}({pa.Name})"); }
            void NoteReference(string? baseType, string where)
            {
                if (baseType is null || CHeader_AbiModel.IsPrimitive(baseType) || known.Contains(baseType)) return;
                report.Warnings.Add($"{surface.Name}: {where} references '{baseType}', which is not in the wanted list (add it, or it maps to an opaque pointer only)");
            }
            declarations.Surfaces.Add(decl);
        }
        report.Skipped.Sort((a, b) => string.CompareOrdinal(a.Header, b.Header) != 0 ? string.CompareOrdinal(a.Header, b.Header) : a.Line.CompareTo(b.Line));
        return new(report.Errors.Count == 0 ? declarations : null, report);
    }

    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>HJSON (comments, unquoted keys) → typed model via YeetJson, same path as FluidUI's compilers.</summary>
    public static T ParseHjson<T>(string hjsonText, string sourceNameForErrors)
    {
        var structure = new StructuralAnalyzer().Analyze(hjsonText);
        if (structure.StructuralErrors.Count != 0) throw new InvalidDataException($"{sourceNameForErrors}: invalid HJSON structure: {string.Join("; ", structure.StructuralErrors)}");
        var parsed = new HjsonContentParser(new HjsonParserOptions()).Parse(hjsonText, structure);
        using JsonDocument? document = parsed.ParsedDocument;
        if (document is null || parsed.SemanticErrors.Count != 0)
            throw new InvalidDataException($"{sourceNameForErrors}: invalid HJSON content: {string.Join("; ", parsed.SemanticErrors)}");
        return document.RootElement.Deserialize<T>(new JsonSerializerOptions(JsonOptions) { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException($"{sourceNameForErrors}: empty document");
    }

    public static void WriteFileAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}