// Spec 71 `normalize`: committed CHeader_Declarations → validated render model (Bindings.ytdata.hjson) for the YeetCode templates.
// All C# naming (D8), all layouts (D5), all calling-convention strings (D6) are decided HERE, once, so templates stay dumb.
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AN.Audio.BindingsCompiler;

public static class CHeader_DeclarationCompiler
{
    public static string NormalizeHjsonToJson(string declarationsHjson, string sourceName)
    {
        var decl = CHeader_Extractor.ParseHjson<CHeader_Declarations>(declarationsHjson, sourceName);
        var model = Normalize(decl);
        return JsonSerializer.Serialize(model, CHeader_Extractor.JsonOptions) + "\n";
    }

    public static RenderModel Normalize(CHeader_Declarations decl)
    {
        var model = new RenderModel { ToolVersion = CHeader_Extractor.ToolVersion };
        foreach (var surface in decl.Surfaces) model.Surfaces.Add(NormalizeSurface(surface));
        return model;
    }

    private static RenderSurface NormalizeSurface(CHeader_SurfaceDeclarations s)
    {
        if (s.Abis.Count == 0) throw new InvalidDataException($"{s.Name}: at least one ABI is required");
        string CsTypeName(string headerName)
        {
            string n = headerName;
            foreach (string p in s.StripPrefixes) if (n.StartsWith(p, StringComparison.Ordinal) && n.Length > p.Length) { n = n[p.Length..]; break; }
            return s.CsPrefix + n;
        }
        var abis = s.Abis.Select(CHeader_AbiModel.For).ToList();
        foreach (var abi in abis) abi.Load(s, CsTypeName);
        var primary = abis[0];

        // Duplicate C# names are a wanted-list bug.
        var csNames = new HashSet<string>(StringComparer.Ordinal);
        void Unique(string csName, string what) { if (!csNames.Add(csName)) throw new InvalidDataException($"{s.Name}: duplicate generated type name '{csName}' ({what})"); }

        var rs = new RenderSurface
        {
            Name = s.Name, CsNamespace = s.CsNamespace, CsPrefix = s.CsPrefix,
            Headers = s.Headers.Select(h => new RenderHeader(h.Path, h.Sha256)).ToList(),
            Abis = abis.Select((a, i) => new RenderAbi(a.Name, a.RuntimeCondition, i == 0)).ToList(),
            LayoutsCsName = s.CsPrefix + "Layouts",
        };

        foreach (var e in s.Enums)
        {
            Unique(e.CsName, "enum " + (e.Name ?? e.Entries.FirstOrDefault()?.Name ?? "?"));
            string underlying = "int";
            if (e.Typedef is not null)
            {
                var td = s.Typedefs.FirstOrDefault(t => t.Name == e.Typedef) ?? throw new InvalidDataException($"{s.Name}: enum {e.CsName}: typedef '{e.Typedef}' missing from declarations");
                var r = primary.Resolve(td.Type);
                if (r.Kind != CHeader_AbiModel.Kind.Primitive) throw new InvalidDataException($"{s.Name}: enum {e.CsName}: typedef '{e.Typedef}' is not an integer type");
                underlying = r.CsType;
                if (abis.Any(a => a.Resolve(td.Type).CsType != underlying)) throw new InvalidDataException($"{s.Name}: enum {e.CsName}: underlying type differs between ABIs (nint-sized enum) — not representable");
            }
            rs.Enums.Add(new RenderEnum(e.CsName, e.Name, e.Typedef, e.Flags, underlying, e.Header, e.Line,
                e.Entries.Select(x => new RenderEnumEntry(x.Name, x.Value, x.Expression)).ToList()));
        }

        foreach (var st in s.Structs)
        {
            string csName = CsTypeName(st.Name);
            Unique(csName, "struct " + st.Name);
            var pl = primary.LayoutOf(st);
            var fields = new List<RenderField>();
            foreach (var fl in pl.Fields)
            {
                var f = fl.Field;
                string cDecl = f.FunctionPointer is { } fp
                    ? $"{fp.ReturnType} (*{f.Name})({string.Join(", ", fp.Params.Select(p => p.Type + " " + p.Name))})"
                    : $"{f.Type} {f.Name}" + (f.ArrayLength > 0 ? $"[{f.ArrayLength}]" : "");
                if (f.ArrayLength > 0 && fl.CsFixedElementType is null)
                {
                    // Array of pointers/structs: C# fixed buffers allow primitives only → expand to Name0..NameN-1 (ASIOBufferInfo.buffers[2]).
                    var r = primary.Resolve(f.Type!);
                    if (r.Kind != CHeader_AbiModel.Kind.Pointer) throw new InvalidDataException($"{s.Name}: {st.Name}.{f.Name}: arrays of {r.Kind} are not supported by the generator");
                    for (int i = 0; i < f.ArrayLength; i++)
                        fields.Add(new RenderField(f.Name + i, Pascal(f.Name) + i, "nint", false, null, 0, false, cDecl + (i == 0 ? "" : " (cont.)"), true, i));
                    continue;
                }
                string csType = fl.CsType;
                if (f.Type is { PointerDepth: > 0 } && f.FunctionPointer is null && csType.StartsWith("void*", StringComparison.Ordinal)) csType = "nint"; // void* fields as nint (blittable, no unsafe at use sites)
                fields.Add(new RenderField(f.Name, Pascal(f.Name), csType, fl.CsFixedElementType is not null, fl.CsFixedElementType, f.ArrayLength, f.FunctionPointer is not null, cDecl, false, 0));
            }
            var layouts = new List<RenderLayout>();
            foreach (var abi in abis)
            {
                var l = abi.LayoutOf(st);
                var offsets = new List<RenderOffset>();
                foreach (var fl in l.Fields)
                {
                    if (fl.Field.ArrayLength > 0 && fl.CsFixedElementType is null)
                        for (int i = 0; i < fl.Field.ArrayLength; i++) offsets.Add(new(Pascal(fl.Field.Name) + i, fl.Offset + i * abi.PointerSize));
                    else offsets.Add(new(Pascal(fl.Field.Name), fl.Offset));
                }
                layouts.Add(new RenderLayout(abi.Name, abi.RuntimeCondition, l.Size, offsets));
            }
            rs.Structs.Add(new RenderStruct(csName, st.Name, st.Pack, st.Header, st.Line, fields, layouts));
        }

        foreach (var c in s.Classes)
        {
            Unique(c.CsName, "class " + c.Name);
            string conv = c.CallConv.ToLowerInvariant() switch { "thiscall" => "Thiscall", "stdcall" => "Stdcall", "cdecl" => "Cdecl", _ => throw new InvalidDataException($"{s.Name}: class {c.Name}: unknown callConv '{c.CallConv}'") };
            var methods = new List<RenderMethod>();
            foreach (var m in c.Methods)
            {
                bool returnsVoid = m.ReturnType.Base == "void" && m.ReturnType.PointerDepth == 0;
                string csReturn = returnsVoid ? "void" : primary.Resolve(m.ReturnType).CsType;
                var ps = m.Params.Select(p => new RenderParam(SafeIdent(p.Name), primary.Resolve(p.Type).CsType, p.Type.ToString())).ToList();
                string fnPtr = $"delegate* unmanaged[{conv}]<nint{string.Concat(ps.Select(p => ", " + p.CsType))}, {csReturn}>";
                string paramList = "nint self" + string.Concat(ps.Select(p => $", {p.CsType} {p.Name}"));
                string argList = "self" + string.Concat(ps.Select(p => ", " + p.Name));
                string cDecl = $"{m.ReturnType} {m.Name}({string.Join(", ", m.Params.Select(p => p.Type + " " + p.Name))})";
                methods.Add(new RenderMethod(m.Name, Pascal(m.Name), m.Slot, csReturn, returnsVoid, ps, fnPtr, paramList, argList, cDecl));
            }
            rs.Classes.Add(new RenderClass(c.CsName, c.Name, c.Base, c.BaseSlots, conv, c.Base == "IUnknown" && c.BaseSlots == 3, c.Header, c.Line, methods));
        }
        return rs;
    }

    private static string Pascal(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    private static readonly HashSet<string> CsKeywords = ["string", "params", "object", "ref", "out", "in", "event", "base", "this", "value", "default", "fixed", "lock", "checked"];
    private static string SafeIdent(string name) => CsKeywords.Contains(name) ? "@" + name : name;

    // ─── render model (what the templates see) ──────────────────────────────────────────────────────────────────────────────────────────────────

    public sealed class RenderModel
    {
        public string ToolVersion { get; set; } = "";
        public List<RenderSurface> Surfaces { get; set; } = [];
    }
    public sealed class RenderSurface
    {
        public string Name { get; set; } = "";
        public string CsNamespace { get; set; } = "";
        public string CsPrefix { get; set; } = "";
        public string LayoutsCsName { get; set; } = "";
        public List<RenderHeader> Headers { get; set; } = [];
        public List<RenderAbi> Abis { get; set; } = [];
        public List<RenderEnum> Enums { get; set; } = [];
        public List<RenderStruct> Structs { get; set; } = [];
        public List<RenderClass> Classes { get; set; } = [];
    }
    public sealed record RenderHeader(string Path, string Sha256);
    public sealed record RenderAbi(string Name, string RuntimeCondition, bool IsPrimary);
    public sealed record RenderEnum(string CsName, string? HeaderName, string? Typedef, bool Flags, string Underlying, string Header, int Line, List<RenderEnumEntry> Entries);
    public sealed record RenderEnumEntry(string Name, long Value, string? Expression);
    public sealed record RenderStruct(string CsName, string HeaderName, int Pack, string Header, int Line, List<RenderField> Fields, List<RenderLayout> Layouts);
    public sealed record RenderField(string Name, string CsName, string CsType, bool IsFixed, string? FixedElementType, int ArrayLength, bool IsFunctionPointer, string CDecl, bool IsExpandedArrayElement, int ExpandedIndex);
    public sealed record RenderLayout(string Abi, string RuntimeCondition, int Size, List<RenderOffset> Offsets);
    public sealed record RenderOffset(string CsName, int Offset);
    public sealed record RenderClass(string CsName, string HeaderName, string Base, int BaseSlots, string CallConv, bool BaseIsIUnknown, string Header, int Line, List<RenderMethod> Methods);
    public sealed record RenderMethod(string Name, string CsName, int Slot, string CsReturn, bool ReturnsVoid, List<RenderParam> Params, string FnPtrType, string ParamList, string ArgList, string CDecl);
    public sealed record RenderParam(string Name, string CsType, string CType);
}