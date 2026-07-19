using System;

namespace Buckminster.Usergame.Demo;

// The demo's root module: no dependencies, one piece of state. Deliberately its own counter rather than a read of Engine.TickCount, so the Reporter's dependency on it carries real data instead of being decorative.
public sealed class ModuleDemoClock : IModule
{
    public int Ticks { get; private set; }

    public Type[] Dependencies
    {
        get { return Type.EmptyTypes; }
    }

    public void Initialize()
    {
        Log.Info("ModuleDemoClock initialized");
    }

    public void Shutdown()
    {
    }

    public void PumpEvents()
    {
    }

    public void Tick(double dt)
    {
        Ticks += 1;
    }
}
