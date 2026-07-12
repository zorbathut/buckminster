using System;
using Buckminster;

namespace Buckminster.Host.Desktop;

// The desktop host: owns the loop, the engine is the callee (PLAN.md tick-as-callee). The M4 demo modules and the determinism-harness replay mode arrive with the harness chunk; this is the minimal real hosting shape until then.
internal static class Program
{
    private static void Main()
    {
        using Engine engine = Engine.Create(new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 1024, LogStderrLevelMax = (int)LogLevel.Warn }, (level, message) => Console.WriteLine($"[{level}] {message}"));
        while (!engine.IsReady)
        {
            engine.PumpEvents();
        }
        engine.Tick(1.0 / 60.0);
        engine.Render();
        Console.WriteLine($"Buckminster.Host.Desktop: engine up, ticked to {engine.TickCount}, shutting down clean");
    }
}
