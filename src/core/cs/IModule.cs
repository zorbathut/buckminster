using System;

namespace Buckminster;

// A registered engine module: declares dependencies by concrete type, initializes in dependency (topological) order, pumps and ticks in registration order, shuts down in reverse of the recorded init order. Modules are compile-time composition (PLAN.md: classes/assemblies handed to Engine.Initialize in the host-assembled boot list) -- there is no dynamic loading. The globals (Engine, Log, MainLoop) are ambient statics, so hooks carry no engine parameter.
public interface IModule
{
    // Concrete types of modules this one needs initialized before it. Exact-type matching: the registry is keyed by concrete type, and assignability walking is a feature nobody has needed.
    Type[] Dependencies { get; }

    void Initialize();

    // Called by Engine.Shutdown in reverse recorded init order -- only for modules whose Initialize actually completed. Resource teardown goes here; process exit without Shutdown leaves cleanup to the OS.
    void Shutdown();

    // Called by Engine.PumpEvents -- but only after this module's Initialize has completed (pinned contract: the init pump runs NO module pumps; if async init ever spans multiple pumps, the initialized-prefix question gets settled against this floor). The platform module's winit pump rides here; pump/tick separation means events can flow while the sim is paused. An explicit member rather than a default interface method: matches Tick's shape and stays born-convertible for the future #[buck_trait] vtable.
    void PumpEvents();

    void Tick(double dt);
}
