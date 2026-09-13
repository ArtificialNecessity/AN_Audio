// Spec 71 D7 — independent ABI oracle. Compiled by Asio_AbiProbeTests with the REAL Steinberg headers (never committed) using cl.exe,
// then executed; its stdout is diffed against the bindings compiler's ABI model. If the model and MSVC disagree, the model is wrong.
//
//   SIZE   <struct> <bytes>
//   OFFSET <struct> <field> <bytes>
//   SLOT   <method> <vtable-index>       (IASIO, via a shadow class with the same virtual declaration order)
//   PTR    <pointer-size>
#include <cstdio>
#include <cstddef>
#include <windows.h>
#include "asiosys.h"
#include "asio.h"
#include "iasiodrv.h"

#define SIZE(T)        std::printf("SIZE %s %zu\n", #T, sizeof(T))
#define OFFSET(T, f)   std::printf("OFFSET %s %s %zu\n", #T, #f, offsetof(T, f))

static int g_slot = -1;
// Same single-inheritance-from-IUnknown shape and the same virtual declaration order as IASIO: slot numbers are therefore identical.
// Every override records its own slot; the probe calls each IASIO method through a shadow object and prints what fired.
struct Shadow : public IUnknown
{
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void**) override { g_slot = 0; return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { g_slot = 1; return 1; }
    ULONG STDMETHODCALLTYPE Release() override { g_slot = 2; return 1; }
    virtual ASIOBool init(void*) { g_slot = 3; return 0; }
    virtual void getDriverName(char*) { g_slot = 4; }
    virtual long getDriverVersion() { g_slot = 5; return 0; }
    virtual void getErrorMessage(char*) { g_slot = 6; }
    virtual ASIOError start() { g_slot = 7; return 0; }
    virtual ASIOError stop() { g_slot = 8; return 0; }
    virtual ASIOError getChannels(long*, long*) { g_slot = 9; return 0; }
    virtual ASIOError getLatencies(long*, long*) { g_slot = 10; return 0; }
    virtual ASIOError getBufferSize(long*, long*, long*, long*) { g_slot = 11; return 0; }
    virtual ASIOError canSampleRate(ASIOSampleRate) { g_slot = 12; return 0; }
    virtual ASIOError getSampleRate(ASIOSampleRate*) { g_slot = 13; return 0; }
    virtual ASIOError setSampleRate(ASIOSampleRate) { g_slot = 14; return 0; }
    virtual ASIOError getClockSources(ASIOClockSource*, long*) { g_slot = 15; return 0; }
    virtual ASIOError setClockSource(long) { g_slot = 16; return 0; }
    virtual ASIOError getSamplePosition(ASIOSamples*, ASIOTimeStamp*) { g_slot = 17; return 0; }
    virtual ASIOError getChannelInfo(ASIOChannelInfo*) { g_slot = 18; return 0; }
    virtual ASIOError createBuffers(ASIOBufferInfo*, long, long, ASIOCallbacks*) { g_slot = 19; return 0; }
    virtual ASIOError disposeBuffers() { g_slot = 20; return 0; }
    virtual ASIOError controlPanel() { g_slot = 21; return 0; }
    virtual ASIOError future(long, void*) { g_slot = 22; return 0; }
    virtual ASIOError outputReady() { g_slot = 23; return 0; }
};

#define SLOT(call) do { g_slot = -1; p->call; std::printf("SLOT %s %d\n", #call, g_slot); } while (0)

int main()
{
    std::printf("PTR %zu\n", sizeof(void*));
    SIZE(ASIOSamples); OFFSET(ASIOSamples, hi); OFFSET(ASIOSamples, lo);
    SIZE(ASIOTimeStamp);
    SIZE(ASIOClockSource); OFFSET(ASIOClockSource, isCurrentSource); OFFSET(ASIOClockSource, name);
    SIZE(ASIOChannelInfo); OFFSET(ASIOChannelInfo, channel); OFFSET(ASIOChannelInfo, isInput); OFFSET(ASIOChannelInfo, isActive); OFFSET(ASIOChannelInfo, channelGroup); OFFSET(ASIOChannelInfo, type); OFFSET(ASIOChannelInfo, name);
    SIZE(ASIOBufferInfo); OFFSET(ASIOBufferInfo, isInput); OFFSET(ASIOBufferInfo, channelNum); OFFSET(ASIOBufferInfo, buffers);
    SIZE(ASIOTimeCode); OFFSET(ASIOTimeCode, speed); OFFSET(ASIOTimeCode, timeCodeSamples); OFFSET(ASIOTimeCode, flags); OFFSET(ASIOTimeCode, future);
    SIZE(AsioTimeInfo); OFFSET(AsioTimeInfo, speed); OFFSET(AsioTimeInfo, systemTime); OFFSET(AsioTimeInfo, samplePosition); OFFSET(AsioTimeInfo, sampleRate); OFFSET(AsioTimeInfo, flags); OFFSET(AsioTimeInfo, reserved);
    SIZE(ASIOTime); OFFSET(ASIOTime, reserved); OFFSET(ASIOTime, timeInfo); OFFSET(ASIOTime, timeCode);
    SIZE(ASIOCallbacks); OFFSET(ASIOCallbacks, bufferSwitch); OFFSET(ASIOCallbacks, sampleRateDidChange); OFFSET(ASIOCallbacks, asioMessage); OFFSET(ASIOCallbacks, bufferSwitchTimeInfo);

    Shadow s; IASIO* p = reinterpret_cast<IASIO*>(&s);
    SLOT(init(nullptr)); SLOT(getDriverName(nullptr)); SLOT(getDriverVersion()); SLOT(getErrorMessage(nullptr));
    SLOT(start()); SLOT(stop()); SLOT(getChannels(nullptr, nullptr)); SLOT(getLatencies(nullptr, nullptr));
    SLOT(getBufferSize(nullptr, nullptr, nullptr, nullptr)); SLOT(canSampleRate(0)); SLOT(getSampleRate(nullptr)); SLOT(setSampleRate(0));
    SLOT(getClockSources(nullptr, nullptr)); SLOT(setClockSource(0)); SLOT(getSamplePosition(nullptr, nullptr)); SLOT(getChannelInfo(nullptr));
    SLOT(createBuffers(nullptr, 0, 0, nullptr)); SLOT(disposeBuffers()); SLOT(controlPanel()); SLOT(future(0, nullptr)); SLOT(outputReady());
    return 0;
}