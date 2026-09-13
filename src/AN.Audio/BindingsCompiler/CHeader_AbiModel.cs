// Spec 71 D5 — the ABI model: sizeof/alignof/offsetof for the declared structs under a named target ABI, honouring #pragma pack,
// plus the C → C# type mapping. AN.Audio is AnyCPU, so every layout is computed for x64 AND x86 and both are asserted at runtime.
// Windows LLP64: `long` is 4 bytes. (sysv-x64 / darwin-arm64 come with spec 60 Phases B/C — `long` 8 there.)
namespace AN.Audio.BindingsCompiler;

public sealed class CHeader_AbiModel
{
    public string Name { get; }
    public int PointerSize { get; }
    public int LongSize { get; }

    private CHeader_AbiModel(string name, int pointerSize, int longSize) { Name = name; PointerSize = pointerSize; LongSize = longSize; }

    public static CHeader_AbiModel For(string abi) => abi switch
    {
        "msvc-x64" => new(abi, 8, 4),
        "msvc-x86" => new(abi, 4, 4),
        "sysv-x64" => new(abi, 8, 8),
        "darwin-arm64" => new(abi, 8, 8),
        _ => throw new InvalidDataException($"unknown ABI '{abi}' (known: msvc-x64, msvc-x86, sysv-x64, darwin-arm64)"),
    };

    /// <summary>C# runtime check the generated AssertLayouts uses to pick which ABI's constants apply.</summary>
    public string RuntimeCondition => PointerSize == 8 ? "sizeof(nint) == 8" : "sizeof(nint) == 4";

    // ─── primitives ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> Canonical = new(StringComparer.Ordinal)
    {
        ["char"] = "char", ["signed char"] = "char", ["unsigned char"] = "uchar",
        ["short"] = "short", ["short int"] = "short", ["signed short"] = "short", ["unsigned short"] = "ushort", ["unsigned short int"] = "ushort",
        ["int"] = "int", ["signed"] = "int", ["signed int"] = "int", ["unsigned"] = "uint", ["unsigned int"] = "uint",
        ["long"] = "long", ["long int"] = "long", ["signed long"] = "long", ["unsigned long"] = "ulong", ["unsigned long int"] = "ulong",
        ["long long"] = "llong", ["long long int"] = "llong", ["unsigned long long"] = "ullong", ["unsigned long long int"] = "ullong", ["__int64"] = "llong",
        ["float"] = "float", ["double"] = "double", ["void"] = "void", ["bool"] = "bool",
    };

    public static bool IsPrimitive(string cBase) => Canonical.ContainsKey(cBase);

    /// <summary>(size, alignment) of a canonical primitive on this ABI.</summary>
    public (int Size, int Align) Primitive(string canonical) => canonical switch
    {
        "char" or "uchar" or "bool" => (1, 1),
        "short" or "ushort" => (2, 2),
        "int" or "uint" or "float" => (4, 4),
        "long" or "ulong" => (LongSize, LongSize),
        "llong" or "ullong" or "double" => (8, 8),
        "void" => throw new InvalidDataException("void has no size"),
        _ => throw new InvalidDataException($"not a primitive: {canonical}"),
    };

    /// <summary>C# spelling of a canonical primitive on this ABI (long → int on Windows, nint on LP64).</summary>
    public string CsPrimitive(string canonical) => canonical switch
    {
        "char" => "byte", "uchar" => "byte", "bool" => "byte",
        "short" => "short", "ushort" => "ushort",
        "int" => "int", "uint" => "uint",
        "long" => LongSize == 4 ? "int" : "nint", "ulong" => LongSize == 4 ? "uint" : "nuint",
        "llong" => "long", "ullong" => "ulong",
        "float" => "float", "double" => "double", "void" => "void",
        _ => throw new InvalidDataException($"not a primitive: {canonical}"),
    };

    // ─── type resolution ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    public enum Kind { Primitive, Struct, Enum, Pointer, Opaque }

    /// <summary>A fully resolved type: what it IS after following typedefs.</summary>
    public sealed record Resolved(Kind Kind, string Canonical, CHeader_StructDecl? Struct, CHeader_EnumDecl? Enum, int Size, int Align, string CsType);

    private readonly Dictionary<string, CHeader_TypedefDecl> _typedefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CHeader_StructDecl> _structs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CHeader_EnumDecl> _enumsByTypedef = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CHeader_EnumDecl> _enumsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _csNames = new(StringComparer.Ordinal); // header struct/enum/class name → C# name
    private readonly Dictionary<CHeader_StructDecl, Layout> _layouts = [];

    public sealed record FieldLayout(CHeader_FieldDecl Field, int Offset, int Size, string CsType, string? CsFixedElementType);
    public sealed record Layout(int Size, int Align, List<FieldLayout> Fields);

    public void Load(CHeader_SurfaceDeclarations surface, Func<string, string> csNameOf)
    {
        foreach (var t in surface.Typedefs) _typedefs[t.Name] = t;
        foreach (var s in surface.Structs) { _structs[s.Name] = s; _csNames[s.Name] = csNameOf(s.Name); }
        foreach (var e in surface.Enums) { if (e.Typedef is not null) _enumsByTypedef[e.Typedef] = e; if (e.Name is not null) _enumsByName[e.Name] = e; _csNames[e.CsName] = e.CsName; }
        foreach (var c in surface.Classes) _csNames[c.Name] = c.CsName;
    }

    public Resolved Resolve(CHeader_TypeRef type)
    {
        if (type.PointerDepth > 0)
        {
            var inner = type with { PointerDepth = 0 };
            string cs;
            if (inner.Base == "void") cs = "void";
            else
            {
                Resolved? r = TryResolve(inner);
                cs = r?.CsType ?? "void"; // unknown pointee → opaque void*
            }
            return new(Kind.Pointer, "pointer", null, null, PointerSize, PointerSize, cs + new string('*', type.PointerDepth));
        }
        return TryResolve(type) ?? throw new InvalidDataException($"unresolvable type '{type}' — add it to the wanted list");
    }

    private Resolved? TryResolve(CHeader_TypeRef type, int depth = 0)
    {
        if (depth > 16) throw new InvalidDataException($"typedef cycle at '{type}'");
        string b = type.Base;
        if (_enumsByTypedef.TryGetValue(b, out var te))
        {
            // typedef long X + wanted enum bound to X: the storage is the typedef's underlying primitive, the C# type is the enum
            var under = Resolve(_typedefs[b].Type);
            return new(Kind.Enum, under.Canonical, null, te, under.Size, under.Align, te.CsName);
        }
        if (Canonical.TryGetValue(b, out string? canon))
        {
            var (sz, al) = canon == "void" ? (0, 1) : Primitive(canon);
            return new(Kind.Primitive, canon, null, null, sz, al, CsPrimitive(canon));
        }
        if (_structs.TryGetValue(b, out var s))
        {
            var l = LayoutOf(s);
            return new(Kind.Struct, b, s, null, l.Size, l.Align, _csNames[b]);
        }
        if (_enumsByName.TryGetValue(b, out var ne))
            return new(Kind.Enum, "int", null, ne, 4, 4, ne.CsName); // C enum storage: int on MSVC
        if (_typedefs.TryGetValue(b, out var td))
        {
            if (td.Type.PointerDepth > 0) return Resolve(td.Type);
            return TryResolve(td.Type, depth + 1);
        }
        if (_csNames.TryGetValue(b, out string? cls))
            return new(Kind.Opaque, b, null, null, 0, 1, cls); // a class: only meaningful behind a pointer
        return null;
    }

    public Layout LayoutOf(CHeader_StructDecl s)
    {
        if (_layouts.TryGetValue(s, out var cached)) return cached;
        int offset = 0, maxAlign = 1;
        var fields = new List<FieldLayout>();
        foreach (var f in s.Fields)
        {
            int size, align; string cs; string? fixedElem = null;
            if (f.FunctionPointer is not null)
            {
                size = align = PointerSize;
                cs = FunctionPointerCsType(f.FunctionPointer, "Cdecl");
            }
            else
            {
                var r = Resolve(f.Type!);
                size = r.Size; align = r.Align; cs = r.CsType;
                if (f.ArrayLength > 0)
                {
                    if (r.Kind == Kind.Primitive) fixedElem = r.CsType; // `fixed byte Name[32]`
                    size *= f.ArrayLength;
                }
            }
            if (s.Pack > 0) align = Math.Min(align, s.Pack);
            offset = AlignUp(offset, align);
            fields.Add(new(f, offset, size, cs, fixedElem));
            offset += size;
            maxAlign = Math.Max(maxAlign, align);
        }
        var layout = new Layout(AlignUp(offset, maxAlign), maxAlign, fields);
        _layouts[s] = layout;
        return layout;
    }

    /// <summary>`delegate* unmanaged[Cdecl]&lt;p1, p2, ret&gt;` for a C function-pointer signature.</summary>
    public string FunctionPointerCsType(CHeader_FunctionSignature sig, string callConv)
    {
        var parts = sig.Params.Select(p => Resolve(p.Type).CsType).ToList();
        parts.Add(sig.ReturnType.Base == "void" && sig.ReturnType.PointerDepth == 0 ? "void" : Resolve(sig.ReturnType).CsType);
        return $"delegate* unmanaged[{callConv}]<{string.Join(", ", parts)}>";
    }

    private static int AlignUp(int v, int a) => a <= 1 ? v : (v + a - 1) / a * a;
}