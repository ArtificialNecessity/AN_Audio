using AN.Audio.BindingsCompiler;
using Xunit;

namespace AN.Audio.Tests.BindingsCompiler;

/// <summary>Spec 71 D3 — the preprocessor evaluates #if against authored defines only, keeps offsets, tracks pack.</summary>
public class CHeader_PreprocessorTests
{
    private static CHeader_Preprocessor Make(params (string, string)[] defines) =>
        new(defines.ToDictionary(d => d.Item1, d => d.Item2));

    [Fact]
    public void Masked_text_preserves_length_and_newlines()
    {
        const string src = "#ifndef X\n#define X\nint a; // c\n/* b */ int b;\n#endif\n";
        var r = Make().Process("h", src);
        Assert.Equal(src.Length, r.MaskedText.Length);
        Assert.Equal(src.Count(c => c == '\n'), r.MaskedText.Count(c => c == '\n'));
        Assert.Contains("int a;", r.MaskedText);
        Assert.DoesNotContain("// c", r.MaskedText);
        Assert.DoesNotContain("#define", r.MaskedText);
        Assert.Contains("        int b;", r.MaskedText); // comment blanked to spaces, position kept
    }

    [Fact]
    public void If_branches_follow_authored_defines()
    {
        const string src = "#if NATIVE_INT64\ntypedef long long int S;\n#else\ntypedef struct S { unsigned long hi; unsigned long lo; } S;\n#endif\n";
        var r0 = Make(("NATIVE_INT64", "0")).Process("h", src);
        Assert.DoesNotContain("long long", r0.MaskedText); Assert.Contains("unsigned long hi", r0.MaskedText);
        var r1 = Make(("NATIVE_INT64", "1")).Process("h", src);
        Assert.Contains("long long", r1.MaskedText); Assert.DoesNotContain("unsigned long hi", r1.MaskedText);
    }

    [Fact]
    public void Undeclared_symbol_in_if_is_an_error_not_a_zero()
    {
        var pre = Make();
        pre.Process("h", "#if MYSTERY\nint a;\n#endif\n");
        Assert.Contains(pre.Errors, e => e.Contains("undeclared symbol 'MYSTERY'"));
    }

    [Fact]
    public void Defined_and_ifdef_on_unknown_symbols_are_fine()
    {
        var pre = Make(("_WIN32", "1"));
        var r = pre.Process("h", "#if defined(_WIN32) || defined(_WIN64)\nint win;\n#elif BEOS\nint beos;\n#else\nint other;\n#endif\n#ifdef NOPE\nint nope;\n#endif\n");
        Assert.Empty(pre.Errors);
        Assert.Contains("int win;", r.MaskedText);
        Assert.DoesNotContain("int beos;", r.MaskedText);
        Assert.DoesNotContain("int other;", r.MaskedText);
        Assert.DoesNotContain("int nope;", r.MaskedText);
    }

    [Fact]
    public void Header_defines_feed_later_ifs_and_are_reported()
    {
        var pre = Make(("_WIN32", "1"));
        var r = pre.Process("h", "#if defined(_WIN32)\n#define NATIVE_INT64 0\n#endif\n#if NATIVE_INT64\nint yes;\n#else\nint no;\n#endif\n");
        Assert.Empty(pre.Errors);
        Assert.Equal("0", pre.DefinesFromHeaders["NATIVE_INT64"]);
        Assert.Contains("int no;", r.MaskedText);
    }

    [Fact]
    public void Header_define_disagreeing_with_authored_value_is_an_error()
    {
        var pre = Make(("NATIVE_INT64", "1"));
        pre.Process("h", "#define NATIVE_INT64 0\n");
        Assert.Contains(pre.Errors, e => e.Contains("make them agree"));
    }

    [Fact]
    public void Directives_inside_block_comments_are_not_evaluated()
    {
        var pre = Make();
        var r = pre.Process("h", "/*\n#if kCanTimeCode\nnot code\n#endif\n*/\nint a;\n");
        Assert.Empty(pre.Errors);
        Assert.Contains("int a;", r.MaskedText);
    }

    [Fact]
    public void Pragma_pack_push_pop_is_tracked_by_offset()
    {
        const string src = "int before;\n#pragma pack(push,4)\nint inside;\n#pragma pack(pop)\nint after;\n";
        var r = Make().Process("h", src);
        Assert.Equal(2, r.PackTransitions.Count);
        Assert.Equal(4, r.PackTransitions[0].Pack);
        Assert.Equal(0, r.PackTransitions[1].Pack);
        Assert.True(r.PackTransitions[0].Offset < src.IndexOf("int inside", StringComparison.Ordinal));
        Assert.True(r.PackTransitions[1].Offset < src.IndexOf("int after", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("!0", true)]
    [InlineData("defined(A) && !defined(B)", true)]
    [InlineData("A == 1 && B_VAL >= 0x10", true)]
    [InlineData("(A + 1) * 2 == 4", true)]
    public void Expression_evaluator(string expr, bool expected)
    {
        var pre = Make(("A", "1"), ("B_VAL", "0x10"));
        Assert.Equal(expected, pre.Evaluate("h", 1, expr));
        Assert.Empty(pre.Errors);
    }
}