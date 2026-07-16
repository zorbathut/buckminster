namespace Buckminster.Usergame.Demo;

// The usergame's entry convention for now: one static Register the host hands its engine to. A richer entry type (IUsergame or similar) waits for something that needs it -- config ownership, lifecycle hooks, dynamic loading.
public static class UsergameDemo
{
    public static void Register(Engine engine)
    {
        // Clock registered first: Reporter depends on it AND follows it in tick order, so both registry orderings (init topo-sort, tick registration-order) are exercised meaningfully.
        ModuleDemoClock clock = new ModuleDemoClock();
        engine.RegisterModule(clock);
        engine.RegisterModule(new ModuleDemoReporter(clock));
    }
}
