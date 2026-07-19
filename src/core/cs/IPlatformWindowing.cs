using System;
using Buckminster.Ffi;

namespace Buckminster;

// The core-owned seam between ModuleWindow and a host's platform module (M5.75 hoist: the desktop implementation lives in the host-desktop assembly, so core can't name it). Exactly the operations ModuleWindow consumes, nothing speculative. Internal + InternalsVisibleTo by decision: platform providers are host assemblies, which are IVT'd; a future non-IVT'd provider is a named fence, not an accident.
internal interface IPlatformWindowing
{
    void Pump();

    (uint Written, uint Remaining) Poll(Span<PlatformEventRaw> buffer);

    ulong CreateNativeWindow(string title, uint width, uint height);

    void DestroyNativeWindow(ulong window);

    void SetNativeWindowTitle(ulong window, string title);
}
