namespace Buckminster.Usergame.Demo;

// The usergame's entry convention for now: one static Register the host hands its engine to. A richer entry type (IUsergame or similar) waits for something that needs it -- config ownership, lifecycle hooks, dynamic loading.
public static class UsergameDemo
{
    // The headless composition: the M4 done-when demo (100 ticks and out), what the smoke row runs on every matrix pass.
    public static void Register(Engine engine)
    {
        // Clock registered first: Reporter depends on it AND follows it in tick order, so both registry orderings (init topo-sort, tick registration-order) are exercised meaningfully.
        ModuleDemoClock clock = new ModuleDemoClock();
        engine.RegisterModule(clock);
        engine.RegisterModule(new ModuleDemoReporter(clock));
    }

    // The windowed composition (M5): platform + window modules plus the demo module that creates a window and runs until Escape or close. The usergame owns this wiring -- window creation is game setup, the host stays a thin bootstrap.
    public static void RegisterWindowed(Engine engine)
    {
        ModulePlatformDesktop platform = new ModulePlatformDesktop();
        ModuleWindow moduleWindow = new ModuleWindow(platform);
        engine.RegisterModule(platform);
        engine.RegisterModule(moduleWindow);
        engine.RegisterModule(new ModuleDemoWindowed(moduleWindow));
    }
}
