// Spec 71 — data model shared by extract (writes CHeader_Declarations) and normalize (reads it, writes the render model).
// Every type here is JSON-serialised with System.Text.Json (camelCase); the committed Declarations.ytdata.hjson is exactly
// this shape and is a faithful 1:1 record of what the headers declared — no C# names, no layouts (those belong to normalize).
using System.Text.Json.Serialization;

namespace AN.Audio.BindingsCompiler;

// ─── Wanted list (authored) ───────────────────────────────────────────────────────────────────────────────────────

public sealed class CHeader_WantedList
{
    public int SchemaVersion { get; set; } = 1;
    public List<CHeader_WantedSurface> Surfaces { get; set; } = [];
}

public sealed class CHeader_WantedSurface
{
    /// <summary>Surface name, used for generated file names (`Asio` → `Asio.Structs.generated.cs`).</summary>
    public string Name { get; set; } = "";
    /// <summary>Linguistic-keying prefix for every generated C# type (`Asio_`).</summary>
    public string CsPrefix { get; set; } = "";
    /// <summary>Prefixes stripped from header names BEFORE CsPrefix is applied (`ASIOChannelInfo` → `ChannelInfo` → `Asio_ChannelInfo`).</summary>
    public List<string> StripPrefixes { get; set; } = [];
    public string CsNamespace { get; set; } = "";
    /// <summary>Extract-time only. Absolute or repo-relative root the header paths are resolved against. Never written to the report.</summary>
    public string SdkRoot { get; set; } = "";
    /// <summary>ABIs the normalizer computes layouts for: `msvc-x64`, `msvc-x86` (later `sysv-x64`, `darwin-arm64`).</summary>
    public List<string> Abis { get; set; } = [];
    /// <summary>Preprocessor symbols assumed defined (D3). Value is the C token text; empty string = defined with no value.</summary>
    public Dictionary<string, string> Defines { get; set; } = [];
    /// <summary>Headers in processing order, SdkRoot-relative. `#include` of a listed header is followed; anything else is ignored and reported.</summary>
    public List<string> Headers { get; set; } = [];
    public List<string> Typedefs { get; set; } = [];
    public List<string> Structs { get; set; } = [];
    public List<CHeader_WantedEnum> Enums { get; set; } = [];
    public List<CHeader_WantedClass> Classes { get; set; } = [];
}

public sealed class CHeader_WantedEnum
{
    /// <summary>Named enum (`typedef enum ASIOTimeCodeFlags {...}`), or null for an anonymous one identified by <see cref="FirstEntry"/>.</summary>
    public string? Name { get; set; }
    /// <summary>Anonymous enums are the ASIO norm; the first enumerator name must match exactly one enum in the wanted headers.</summary>
    public string? FirstEntry { get; set; }
    public string CsName { get; set; } = "";
    /// <summary>Emit [Flags].</summary>
    public bool Flags { get; set; }
    /// <summary>A `typedef long X;` whose uses should be typed as this enum in generated signatures (ASIOError → Asio_Error).</summary>
    public string? Typedef { get; set; }
}

public sealed class CHeader_WantedClass
{
    public string Name { get; set; } = "";
    public string Base { get; set; } = "IUnknown";
    /// <summary>Vtable slots occupied by the base (IUnknown = 3). Own virtuals start at this index.</summary>
    public int BaseSlots { get; set; } = 3;
    /// <summary>`thiscall` (plain C++ virtuals) or `stdcall` (COM `STDMETHODCALLTYPE`).</summary>
    public string CallConv { get; set; } = "thiscall";
    public string CsName { get; set; } = "";
}

// ─── Declarations (committed boundary, written by extract) ────────────────────────────────────────────────────────

public sealed class CHeader_Declarations
{
    public int SchemaVersion { get; set; } = 1;
    public string Source { get; set; } = "";
    public List<CHeader_SurfaceDeclarations> Surfaces { get; set; } = [];
}

public sealed class CHeader_SurfaceDeclarations
{
    public string Name { get; set; } = "";
    public string CsPrefix { get; set; } = "";
    public List<string> StripPrefixes { get; set; } = [];
    public string CsNamespace { get; set; } = "";
    public List<string> Abis { get; set; } = [];
    public Dictionary<string, string> Defines { get; set; } = [];
    public List<CHeader_HeaderRecord> Headers { get; set; } = [];
    public List<CHeader_TypedefDecl> Typedefs { get; set; } = [];
    public List<CHeader_StructDecl> Structs { get; set; } = [];
    public List<CHeader_EnumDecl> Enums { get; set; } = [];
    public List<CHeader_ClassDecl> Classes { get; set; } = [];
}

public sealed record CHeader_HeaderRecord(string Path, string Sha256);

/// <summary>A C type as written, decomposed: `unsigned long`, `void*`, `ASIOChannelInfo*`, `char` (array length lives on the field).</summary>
public sealed record CHeader_TypeRef(string Base, int PointerDepth)
{
    public override string ToString() => Base + new string('*', PointerDepth);
}

public sealed record CHeader_TypedefDecl(string Name, CHeader_TypeRef Type, string Header, int Line);

public sealed class CHeader_StructDecl
{
    public string Name { get; set; } = "";
    /// <summary>`#pragma pack` in effect at the declaration; 0 = natural alignment.</summary>
    public int Pack { get; set; }
    public List<CHeader_FieldDecl> Fields { get; set; } = [];
    public string Header { get; set; } = "";
    public int Line { get; set; }
}

public sealed class CHeader_FieldDecl
{
    public string Name { get; set; } = "";
    /// <summary>Null when <see cref="FunctionPointer"/> is set.</summary>
    public CHeader_TypeRef? Type { get; set; }
    /// <summary>Fixed array length, or 0 for a scalar.</summary>
    public int ArrayLength { get; set; }
    /// <summary>`ret (*name)(params)` — a function-pointer field (ASIOCallbacks).</summary>
    public CHeader_FunctionSignature? FunctionPointer { get; set; }
}

public sealed record CHeader_ParamDecl(string Name, CHeader_TypeRef Type);
public sealed record CHeader_FunctionSignature(CHeader_TypeRef ReturnType, List<CHeader_ParamDecl> Params);

public sealed class CHeader_EnumDecl
{
    /// <summary>Header tag name; null for anonymous.</summary>
    public string? Name { get; set; }
    public string CsName { get; set; } = "";
    public bool Flags { get; set; }
    public string? Typedef { get; set; }
    public List<CHeader_EnumEntry> Entries { get; set; } = [];
    public string Header { get; set; } = "";
    public int Line { get; set; }
}

/// <summary>Value is the evaluated constant (implicit `previous + 1` resolved), Expression the source text when one was written.</summary>
public sealed record CHeader_EnumEntry(string Name, long Value, string? Expression);

public sealed class CHeader_ClassDecl
{
    public string Name { get; set; } = "";
    public string CsName { get; set; } = "";
    public string Base { get; set; } = "";
    public int BaseSlots { get; set; }
    public string CallConv { get; set; } = "";
    /// <summary>Declaration order = vtable order. Slot = BaseSlots + index.</summary>
    public List<CHeader_MethodDecl> Methods { get; set; } = [];
    public string Header { get; set; } = "";
    public int Line { get; set; }
}

public sealed record CHeader_MethodDecl(string Name, int Slot, CHeader_TypeRef ReturnType, List<CHeader_ParamDecl> Params);

// ─── Extraction report ────────────────────────────────────────────────────────────────────────────────────────────

public sealed class CHeader_ExtractionReport
{
    public string ToolVersion { get; set; } = "";
    public string WantedSha256 { get; set; } = "";
    public List<CHeader_HeaderRecord> Headers { get; set; } = [];
    public bool ConditionalEvaluation { get; set; } = true;
    /// <summary>Every `#define` the headers themselves introduced while processing (asiosys.h → WINDOWS, NATIVE_INT64, …).</summary>
    public Dictionary<string, string> DefinesFromHeaders { get; set; } = [];
    public List<CHeader_Skip> Skipped { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

public sealed record CHeader_Skip(string Header, int Line, string Reason, string Preview);