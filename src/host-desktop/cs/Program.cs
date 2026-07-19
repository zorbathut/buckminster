using System;
using Buckminster;
using Buckminster.Host.Desktop.Ffi;
using Buckminster.Usergame.Demo;

namespace Buckminster.Host.Desktop;

// The desktop host: owns the loop, the engine is the callee (PLAN.md tick-as-callee), and per the four-part model it stays a thin bootstrap -- create the engine, hand it the usergame, drive the cadence until the usergame queues exit. The usergame arrives by static ProjectReference today, the degenerate case of usergame loading. The pump/tick/render loop is hand-composed until the core-provided frame composition exists -- extract it before a second host hosts, per the four-part model bullet.
internal static class Program
{
    private static int Main(string[] args)
    {
        // --headless: the M4 done-when composition (100 ticks and out), what the test-all smoke row runs in display-less CI. No argument: the windowed M5 demo, until Escape or the close button. Anything else is a loud error, not a silent windowed run.
        bool headless = false;
        foreach (string arg in args)
        {
            if (arg == "--headless")
            {
                headless = true;
            }
            else
            {
                Console.Error.WriteLine($"unknown argument '{arg}' (the only recognized argument is --headless)");
                return 1;
            }
        }
        // The multi-crate canary: this assembly's own generated bindings (Ffi/Generated) round-trip through the executor cdylib once at startup, so a broken macro->dump->ffigen->pinvoke pipeline for non-core crates fails the smoke run loudly instead of lurking until the first real host export.
        int probe = Native.TestHostProbe(20);
        if (probe != 41)
        {
            Console.Error.WriteLine($"host probe returned {probe}, expected 41 -- the multi-crate FFI pipeline is broken");
            return 1;
        }
        // Info, not Trace: the Rust log crate is process-global, so winit's internal Debug/Trace records (wayland globals, calloop dispatch) flow through the engine pipeline too -- one pipeline is the design, the level filter is the knob.
        using Engine engine = Engine.Create(new EngineConfig { LogLevelMax = (int)LogLevel.Info, LogBufferCapacity = 1024, LogStderrLevelMax = (int)LogLevel.Warn }, (level, message) => Console.WriteLine($"[{level}] {message}"));
        if (headless)
        {
            UsergameDemo.Register(engine);
        }
        else
        {
            UsergameDemo.RegisterWindowed(engine);
        }
        while (!engine.IsReady)
        {
            engine.PumpEvents();
        }
        while (!engine.ExitQueued)
        {
            engine.PumpEvents();
            engine.Tick(1.0 / 60.0);
            engine.Render();
            if (!headless)
            {
                // Interim host-side pacing: the platform pump is non-blocking, so an unpaced loop is a 100%-CPU spin. Honest placeholder until render pacing/vsync exists (M6).
                System.Threading.Thread.Sleep(10);
            }
        }
        Console.WriteLine($"Buckminster.Host.Desktop: usergame finished after {engine.TickCount} ticks, shutting down clean");
        // The machine-shaped sentinel the test-all smoke row matches exactly (BUCK-TEST-EXIT lineage); the human-readable line above is free to change.
        Console.WriteLine($"BUCK-DEMO-EXIT ticks={engine.TickCount}");
        return 0;
    }
}
