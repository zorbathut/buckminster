using System;

namespace Buckminster.Usergame.Demo;

// The demo's dependent module: declares the Clock dependency (init order runs through the registry's topo sort) and holds the instance by constructor -- explicit boot-list assembly means the usergame wires its own instances; the parameterless-ctor convention belongs to the future discovery model, not this.
public sealed class ModuleDemoReporter : IModule
{
    private const int TicksToRun = 100;

    private readonly ModuleDemoClock clock;

    public ModuleDemoReporter(ModuleDemoClock clock)
    {
        this.clock = clock;
    }

    public Type[] Dependencies
    {
        get { return new[] { typeof(ModuleDemoClock) }; }
    }

    public void Initialize()
    {
        Log.Info($"ModuleDemoReporter initialized; running to {TicksToRun} ticks");
    }

    public void Shutdown()
    {
    }

    public void PumpEvents()
    {
    }

    public void Tick(double dt)
    {
        if (clock.Ticks % 25 == 0)
        {
            Log.Info($"ModuleDemoReporter: {clock.Ticks} ticks");
        }
        if (clock.Ticks >= TicksToRun)
        {
            Log.Info("ModuleDemoReporter: done, queueing exit");
            Engine.QueueExit();
        }
    }
}
