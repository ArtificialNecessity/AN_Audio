using AN.Audio.BindingsCompiler;
using Xunit;

namespace AN.Audio.Tests.BindingsCompiler;

/// <summary>Spec 71 D5 — the ABI model reproduces the spec §5 table for BOTH Windows ABIs from the committed ASIO declarations.</summary>
public class CHeader_AbiModelTests
{
    public static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "AN.Audio.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("AN.Audio.slnx not found above " + AppContext.BaseDirectory);
    }

    public static CHeader_SurfaceDeclarations AsioSurface()
    {
        string path = Path.Combine(RepoRoot(), "src", "AN.Audio", "CodeGen", "Declarations.ytdata.hjson");
        var decl = CHeader_Extractor.ParseHjson<CHeader_Declarations>(File.ReadAllText(path), path);
        return Assert.Single(decl.Surfaces, s => s.Name == "Asio");
    }

    private static CHeader_AbiModel Model(string abi)
    {
        var s = AsioSurface();
        var m = CHeader_AbiModel.For(abi);
        m.Load(s, n => "Asio_" + n.Replace("ASIO", "").Replace("Asio", ""));
        return m;
    }

    private static CHeader_StructDecl Struct(string name) => Assert.Single(AsioSurface().Structs, s => s.Name == name);

    // Spec 71 §5 table. ASIOTime = 148 (not 152) is the pack(4) proof; ASIOBufferInfo/ASIOCallbacks are the pointer-width proof.
    [Theory]
    [InlineData("msvc-x64", "ASIOSamples", 8)]
    [InlineData("msvc-x64", "ASIOTimeStamp", 8)]
    [InlineData("msvc-x64", "ASIOChannelInfo", 52)]
    [InlineData("msvc-x64", "ASIOBufferInfo", 24)]
    [InlineData("msvc-x64", "ASIOClockSource", 48)]
    [InlineData("msvc-x64", "ASIOTimeCode", 84)]
    [InlineData("msvc-x64", "AsioTimeInfo", 48)]
    [InlineData("msvc-x64", "ASIOTime", 148)]
    [InlineData("msvc-x64", "ASIOCallbacks", 32)]
    [InlineData("msvc-x86", "ASIOChannelInfo", 52)]
    [InlineData("msvc-x86", "ASIOBufferInfo", 16)]
    [InlineData("msvc-x86", "ASIOTimeCode", 84)]
    [InlineData("msvc-x86", "ASIOTime", 148)]
    [InlineData("msvc-x86", "ASIOCallbacks", 16)]
    public void Struct_sizes_match_spec_table(string abi, string structName, int expectedSize)
    {
        Assert.Equal(expectedSize, Model(abi).LayoutOf(Struct(structName)).Size);
    }

    [Theory]
    [InlineData("msvc-x64", "ASIOChannelInfo", "name", 20)]
    [InlineData("msvc-x64", "ASIOBufferInfo", "buffers", 8)]
    [InlineData("msvc-x64", "ASIOTimeCode", "flags", 16)]
    [InlineData("msvc-x64", "ASIOTimeCode", "future", 20)]
    [InlineData("msvc-x64", "ASIOTime", "timeInfo", 16)]
    [InlineData("msvc-x64", "ASIOTime", "timeCode", 64)]
    [InlineData("msvc-x64", "AsioTimeInfo", "sampleRate", 24)]
    [InlineData("msvc-x64", "AsioTimeInfo", "reserved", 36)]
    [InlineData("msvc-x86", "ASIOBufferInfo", "buffers", 8)]
    public void Field_offsets(string abi, string structName, string field, int expectedOffset)
    {
        var l = Model(abi).LayoutOf(Struct(structName));
        Assert.Equal(expectedOffset, Assert.Single(l.Fields, f => f.Field.Name == field).Offset);
    }

    [Fact]
    public void Windows_long_is_int_and_typedef_enums_resolve()
    {
        var m = Model("msvc-x64");
        Assert.Equal("int", m.Resolve(new("long", 0)).CsType);
        Assert.Equal("uint", m.Resolve(new("unsigned long", 0)).CsType);
        Assert.Equal("Asio_Error", m.Resolve(new("ASIOError", 0)).CsType);
        Assert.Equal(4, m.Resolve(new("ASIOError", 0)).Size);
        Assert.Equal("double", m.Resolve(new("ASIOSampleRate", 0)).CsType);
        Assert.Equal("byte*", m.Resolve(new("char", 1)).CsType);
        Assert.Equal("Asio_Time*", m.Resolve(new("ASIOTime", 1)).CsType);
        Assert.Equal("void*", m.Resolve(new("void", 1)).CsType);
    }

    [Fact]
    public void Sysv_long_is_nint()
    {
        var m = Model("sysv-x64");
        Assert.Equal("nint", m.Resolve(new("long", 0)).CsType);
        Assert.Equal(8, m.Resolve(new("long", 0)).Size);
    }

    [Fact]
    public void Normalize_produces_vtable_slots_3_to_23_for_IASIO()
    {
        string path = Path.Combine(RepoRoot(), "src", "AN.Audio", "CodeGen", "Declarations.ytdata.hjson");
        var model = CHeader_DeclarationCompiler.Normalize(CHeader_Extractor.ParseHjson<CHeader_Declarations>(File.ReadAllText(path), path));
        var cls = Assert.Single(Assert.Single(model.Surfaces).Classes);
        Assert.Equal("Asio_Driver", cls.CsName);
        Assert.Equal(21, cls.Methods.Count);
        Assert.Equal(Enumerable.Range(3, 21), cls.Methods.Select(m => m.Slot));
        Assert.Equal("init", cls.Methods[0].Name); Assert.Equal("outputReady", cls.Methods[^1].Name);
        Assert.Equal("delegate* unmanaged[Thiscall]<nint, double, Asio_Error>", Assert.Single(cls.Methods, m => m.Name == "canSampleRate").FnPtrType);
    }
}