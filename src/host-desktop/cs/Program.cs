using System;
using Buckminster;
using Buckminster.Usergame.Demo;

namespace Buckminster.Host.Desktop;

// The desktop host: owns the loop, the engine is the callee (PLAN.md tick-as-callee), and per the four-part model it stays a thin bootstrap -- create the engine, hand it the usergame, drive the cadence until the usergame queues exit. The usergame arrives by static ProjectReference today, the degenerate case of usergame loading. The pump/tick/render loop is hand-composed until the core-provided frame composition exists -- extract it before a second host hosts, per the four-part model bullet.
internal static class Program
{
    private static void Main()
    {
        using Engine engine = Engine.Create(new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 1024, LogStderrLevelMax = (int)LogLevel.Warn }, (level, message) => Console.WriteLine($"[{level}] {message}"));
        UsergameDemo.Register(engine);
        while (!engine.IsReady)
        {
            engine.PumpEvents();
        }
        while (!engine.ExitQueued)
        {
            engine.PumpEvents();
            engine.Tick(1.0 / 60.0);
            engine.Render();
        }
        Console.WriteLine($"Buckminster.Host.Desktop: usergame finished after {engine.TickCount} ticks, shutting down clean");
        // The machine-shaped sentinel the test-all smoke row matches exactly (BUCK-TEST-EXIT lineage); the human-readable line above is free to change.
        Console.WriteLine($"BUCK-DEMO-EXIT ticks={engine.TickCount}");
    }
}
