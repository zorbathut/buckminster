using System;
using Buckminster.Ffi;

namespace Buckminster;

// The multi-instantiable unit of render residency (M5.75; device/resources/window attachments arrive at M6 -- until then the instance is deliberately near-empty, and this class exists to pin the handle lifecycle and the Current pattern). Simulation is further up the stack and not a demesne concern. Threading contract (PLAN.md): a single demesne's calls are externally synchronized; different demesnes may be driven concurrently from different threads. The usergame owns its demesne setup for now (owner decision; the demo creates one).
public sealed class Demesne : IDisposable
{
    // Backing field for Current; ThreadStatic can't ride an auto-property.
    [ThreadStatic]
    private static Demesne? currentDemesne;

    // The thread-local ambient demesne (Ghi Environment.Current lineage). Deliberately dormant until M6: nothing engine-side reads it yet -- it is host/test/game-settable, per-thread by design, and becomes the scoping/routing key when Render runs under a demesne.
    public static Demesne? Current
    {
        get { return currentDemesne; }
        set { currentDemesne = value; }
    }

    private ulong handle;
    private bool disposed;

    public Demesne()
    {
        handle = Native.DemesneCreate();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        Native.DemesneDestroy(handle);
        handle = 0;
    }
}
