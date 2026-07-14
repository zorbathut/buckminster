using System;

namespace Buckminster;

// A registered engine module: declares dependencies by concrete type, initializes in dependency (topological) order, ticks in registration order. Modules are compile-time composition (PLAN.md: classes/assemblies registered at startup) -- there is no dynamic loading.
public interface IModule
{
    // Concrete types of modules this one needs initialized before it. Exact-type matching: the registry is keyed by concrete type, and assignability walking is a feature nobody has needed.
    Type[] Dependencies { get; }

    void Initialize(Engine engine);

    void Tick(Engine engine, double dt);
}
