using AN.Audio.BindingsCompiler;
using Xunit;

namespace AN.Audio.Tests.BindingsCompiler;

/// <summary>Spec 71 D4 — the C-subset parser on synthetic headers shaped like asio.h / iasiodrv.h.</summary>
public class CHeader_ParserTests
{
    private static CHeader_Parser.ParseResult Parse(string header, params (string, string)[] defines)
    {
        var pre = new CHeader_Preprocessor(defines.ToDictionary(d => d.Item1, d => d.Item2));
        var r = pre.Process("fixture.h", header);
        Assert.Empty(pre.Errors);
        return new CHeader_Parser("fixture.h", r.MaskedText, r.PackTransitions, pre.Defines).Parse();
    }

    [Fact]
    public void Typedefs_scalar_and_pointer()
    {
        var r = Parse("typedef long ASIOBool;\ntypedef double ASIOSampleRate;\ntypedef long long int Big;\ntypedef unsigned long UL;\ntypedef void* Handle;\n");
        Assert.Equal(["ASIOBool", "ASIOSampleRate", "Big", "UL", "Handle"], r.Typedefs.Select(t => t.Name));
        Assert.Equal("long", r.Typedefs[0].Type.Base);
        Assert.Equal("long long int", r.Typedefs[2].Type.Base);
        Assert.Equal("unsigned long", r.Typedefs[3].Type.Base);
        Assert.Equal(new CHeader_TypeRef("void", 1), r.Typedefs[4].Type);
    }

    [Fact]
    public void Struct_with_arrays_pointers_and_pack()
    {
        var r = Parse("#pragma pack(push,4)\ntypedef struct ASIOBufferInfo\n{\n\tASIOBool isInput;\n\tlong channelNum;\n\tvoid *buffers[2];\n} ASIOBufferInfo;\ntypedef struct { char name[32]; } Anon;\n#pragma pack(pop)\nstruct Tail { int x, *py; };\n");
        Assert.Equal(3, r.Structs.Count);
        var bi = r.Structs[0];
        Assert.Equal("ASIOBufferInfo", bi.Name); Assert.Equal(4, bi.Pack); Assert.Equal(2, bi.Line);
        Assert.Equal("ASIOBool", bi.Fields[0].Type!.Base);
        Assert.Equal(new CHeader_TypeRef("void", 1), bi.Fields[2].Type); Assert.Equal(2, bi.Fields[2].ArrayLength);
        Assert.Equal("Anon", r.Structs[1].Name); Assert.Equal(32, r.Structs[1].Fields[0].ArrayLength);
        Assert.Equal(0, r.Structs[2].Pack);
        Assert.Equal(["x", "py"], r.Structs[2].Fields.Select(f => f.Name));
        Assert.Equal(1, r.Structs[2].Fields[1].Type!.PointerDepth);
        Assert.Empty(r.Skips);
    }

    [Fact]
    public void Anonymous_enum_implicit_sequences_hex_and_shifts()
    {
        var r = Parse("typedef long ASIOError;\nenum {\n\tASE_OK = 0,\n\tASE_SUCCESS = 0x3f4847a0,\n\tASE_NotPresent = -1000,\n\tASE_HWMalfunction,\n\tASE_InvalidParameter\n};\ntypedef enum Flags { kA = 1, kB = 1 << 1, kC = 1 << 8 } Flags;\nenum Named { Z = 5, Z2 };\n");
        Assert.Equal(3, r.Enums.Count);
        var e = r.Enums[0];
        Assert.Null(e.Name);
        Assert.Equal([0L, 0x3f4847a0L, -1000L, -999L, -998L], e.Entries.Select(x => x.Value));
        Assert.Equal("- 1000", e.Entries[2].Expression);
        Assert.Null(e.Entries[3].Expression);
        Assert.Equal("Flags", r.Enums[1].Name); Assert.Equal([1L, 2L, 256L], r.Enums[1].Entries.Select(x => x.Value));
        Assert.Equal("Named", r.Enums[2].Name); Assert.Equal(6L, r.Enums[2].Entries[1].Value);
    }

    [Fact]
    public void Function_pointer_fields()
    {
        var r = Parse("typedef struct ASIOCallbacks\n{\n\tvoid (*bufferSwitch) (long doubleBufferIndex, ASIOBool directProcess);\n\tlong (*asioMessage) (long selector, long value, void* message, double* opt);\n\tASIOTime* (*bufferSwitchTimeInfo) (ASIOTime* params, long doubleBufferIndex, ASIOBool directProcess);\n} ASIOCallbacks;\n");
        var cb = Assert.Single(r.Structs);
        Assert.Equal(3, cb.Fields.Count);
        var bs = cb.Fields[0].FunctionPointer!;
        Assert.Equal("void", bs.ReturnType.Base); Assert.Equal(2, bs.Params.Count); Assert.Equal("directProcess", bs.Params[1].Name);
        var ti = cb.Fields[2].FunctionPointer!;
        Assert.Equal(new CHeader_TypeRef("ASIOTime", 1), ti.ReturnType);
        Assert.Equal(new CHeader_TypeRef("ASIOTime", 1), ti.Params[0].Type);
        Assert.Equal(new CHeader_TypeRef("double", 1), cb.Fields[1].FunctionPointer!.Params[3].Type);
    }

    [Fact]
    public void Cpp_interface_with_pure_virtuals_in_declaration_order()
    {
        var r = Parse("interface IASIO : public IUnknown\n{\n\tvirtual ASIOBool init(void *sysHandle) = 0;\n\tvirtual void getDriverName(char *name) = 0;\t\n\tvirtual long getDriverVersion() = 0;\n\tvirtual ASIOError getBufferSize(long *minSize, long *maxSize,\n\t\tlong *preferredSize, long *granularity) = 0;\n\tvirtual ASIOError canSampleRate(ASIOSampleRate sampleRate) = 0;\n};\n");
        var c = Assert.Single(r.Classes);
        Assert.Equal("IASIO", c.Name); Assert.Equal("IUnknown", c.Base);
        Assert.Equal(["init", "getDriverName", "getDriverVersion", "getBufferSize", "canSampleRate"], c.Methods.Select(m => m.Name));
        Assert.Equal([0, 1, 2, 3, 4], c.Methods.Select(m => m.Slot)); // extractor adds baseSlots
        Assert.Equal(new CHeader_TypeRef("void", 1), c.Methods[0].Params[0].Type);
        Assert.Equal(4, c.Methods[3].Params.Count);
        Assert.Equal(new CHeader_TypeRef("long", 1), c.Methods[3].Params[3].Type);
        Assert.Equal("void", c.Methods[1].ReturnType.Base);
    }

    [Fact]
    public void Unrecognised_text_is_skipped_and_reported_not_guessed()
    {
        var r = Parse("ASIOError ASIOInit(ASIODriverInfo *info);\ntypedef interface IASIO IASIO;\ntemplate<class T> struct Weird { T t; };\ntypedef long Fine;\n");
        Assert.Single(r.Typedefs); Assert.Equal("Fine", r.Typedefs[0].Name);
        Assert.Contains(r.Skips, s => s.Reason.Contains("function prototype") && s.Line == 1);
        Assert.Contains(r.Skips, s => s.Reason.Contains("typedef interface") && s.Line == 2);
        Assert.Contains(r.Skips, s => s.Line == 3);
        Assert.Empty(r.Errors);
    }
}