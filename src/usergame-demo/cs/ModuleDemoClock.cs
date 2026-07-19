using System;

namespace Buckminster.Usergame.Demo;

// The demo's root module: no dependencies, one piece of state. Deliberately its own counter rather than a read of Engine.TickCount, so the Reporter's dependency on it carries real data instead of being decorative. Also the demo's demesne owner: the usergame sets up its own environment for now (M5.75 owner decision) -- create in Initialize, publish as the ambient Current, tear down in Shutdown.
public sealed class ModuleDemoClock : IModule
{
    public int Ticks { get; private set; }

    private Demesne? demesne;

    public Type[] Dependencies
    {
        get { return Type.EmptyTypes; }
    }

    public void Initialize()
    {
        demesne = new Demesne();
        Demesne.Current = demesne;
        Log.Info("ModuleDemoClock initialized");
    }

    public void Shutdown()
    {
        Demesne.Current = null;
        demesne!.Dispose();
    }

    public void PumpEvents()
    {
    }

    public void Tick(double dt)
    {
        Ticks += 1;
    }
}
