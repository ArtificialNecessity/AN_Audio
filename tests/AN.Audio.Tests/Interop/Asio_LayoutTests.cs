using System.Runtime.InteropServices;
using AN.Audio.Platforms.Windows.Asio;
using Xunit;

namespace AN.Audio.Tests.Interop;

/// <summary>Spec 71 — the GENERATED C# mirrors have the layouts the model computed (runtime AssertLayouts) and the header's constants
/// (overview rule 1: every enum value asserted against the SDK value it names — numbers from _EXTERNAL_APIS/ASIO_IASIO.md).</summary>
public unsafe class Asio_LayoutTests
{
    [Fact]
    public void AssertLayouts_passes_for_the_running_bitness() => Asio_Layouts.AssertLayouts();

    [Fact]
    public void Struct_sizes_for_x64()
    {
        if (sizeof(nint) != 8) return;
        Assert.Equal(52, sizeof(Asio_ChannelInfo));
        Assert.Equal(24, sizeof(Asio_BufferInfo));
        Assert.Equal(48, sizeof(Asio_ClockSource));
        Assert.Equal(84, sizeof(Asio_TimeCode));
        Assert.Equal(48, sizeof(Asio_TimeInfo));
        Assert.Equal(148, sizeof(Asio_Time));
        Assert.Equal(32, sizeof(Asio_Callbacks));
        Assert.Equal(8, sizeof(Asio_Samples));
    }

    [Fact]
    public void Enum_values_match_the_sdk_header()
    {
        Assert.Equal(0, (int)Asio_Error.ASE_OK);
        Assert.Equal(0x3f4847a0, (int)Asio_Error.ASE_SUCCESS);
        Assert.Equal(-1000, (int)Asio_Error.ASE_NotPresent);
        Assert.Equal(-995, (int)Asio_Error.ASE_NoClock);
        Assert.Equal(18, (int)Asio_SampleType.ASIOSTInt32LSB);
        Assert.Equal(16, (int)Asio_SampleType.ASIOSTInt16LSB);
        Assert.Equal(19, (int)Asio_SampleType.ASIOSTFloat32LSB);
        Assert.Equal(3, (int)Asio_MessageSelector.kAsioResetRequest);
        Assert.Equal(7, (int)Asio_MessageSelector.kAsioSupportsTimeInfo);
        Assert.Equal(15, (int)Asio_MessageSelector.kAsioOverload);
        Assert.Equal(0x24042012, (int)Asio_FutureSelector.kAsioCanReportOverload);
        Assert.Equal(1 << 8, (int)Asio_TimeCodeFlags.kTcSpeedValid);
        Assert.Equal(1, (int)Asio_Bool.ASIOTrue);
    }

    [Fact]
    public void Vtable_slots_match_iasiodrv_h()
    {
        Assert.Equal(3, Asio_Driver.Vtbl_init);
        Assert.Equal(7, Asio_Driver.Vtbl_start);
        Assert.Equal(11, Asio_Driver.Vtbl_getBufferSize);
        Assert.Equal(18, Asio_Driver.Vtbl_getChannelInfo);
        Assert.Equal(19, Asio_Driver.Vtbl_createBuffers);
        Assert.Equal(23, Asio_Driver.Vtbl_outputReady);
    }

    [Fact]
    public void Typed_wrapper_dispatches_through_the_vtable_slot()
    {
        // A fake object: vtable of 24 slots, slot 7 (start) points at a managed stub. Proves the generated wrapper indexes the right slot
        // and uses a calling convention the runtime can bind (Thiscall == default on x64).
        nint* vtable = stackalloc nint[24];
        for (int i = 0; i < 24; i++) vtable[i] = 0;
        vtable[Asio_Driver.Vtbl_start] = (nint)(delegate* unmanaged[Thiscall]<nint, Asio_Error>)&StartStub;
        nint* obj = stackalloc nint[1];
        obj[0] = (nint)vtable;
        Assert.Equal(Asio_Error.ASE_NoClock, Asio_Driver.Start((nint)obj));
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvThiscall)])]
    private static Asio_Error StartStub(nint self) => Asio_Error.ASE_NoClock;
}