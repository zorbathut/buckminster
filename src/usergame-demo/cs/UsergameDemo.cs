namespace Buckminster.Usergame.Demo;

// The usergame's entry convention for now: module-list factories the host folds into its boot list (M5.75: the HOST assembles the list and constructs any platform modules; the usergame receives core-typed modules and never touches host types). A richer entry type (IUsergame or similar) waits for something that needs it -- config ownership, lifecycle hooks, dynamic loading.
public static class UsergameDemo
{
    // The headless composition: the M4 done-when demo (100 ticks and out), what the smoke row runs on every matrix pass.
    public static IModule[] Modules()
    {
        // Clock listed first: Reporter depends on it AND follows it in tick order, so both registry orderings (init topo-sort, tick list-order) are exercised meaningfully.
        ModuleDemoClock clock = new ModuleDemoClock();
        return new IModule[] { clock, new ModuleDemoReporter(clock) };
    }

    // The windowed composition's usergame half (M5): the demo module that creates a window and runs until Escape or close. The host supplies the ModuleWindow it wired to its platform module -- window PLUMBING is host territory now; window USE stays game setup.
    public static IModule[] ModulesWindowed(ModuleWindow moduleWindow)
    {
        return new IModule[] { new ModuleDemoWindowed(moduleWindow) };
    }
}
