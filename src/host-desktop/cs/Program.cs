using System;
using System.Collections.Generic;
using Buckminster;
using Buckminster.Host.Desktop.Ffi;
using Buckminster.Usergame.Demo;

namespace Buckminster.Host.Desktop;

// The desktop host: the executor (M5.75 host-as-executor -- its Rust crate is also the native link root). Hosts have no interface; this one's whole job is assemble the boot list, Engine.Initialize, drive MainLoop.Iterate at its platform's cadence (an owned loop here; rAF or display-link callbacks on inverted platforms), Engine.Shutdown. Pacing lives in MainLoop -- the host owns only the heartbeat. The usergame arrives by static ProjectReference today, the degenerate case of usergame loading.
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
        // Boot-list assembly is host work (M5.75): the host constructs its platform plumbing and the pre-wired ModuleWindow; the usergame contributes its own modules and reaches windowing only through the core-typed ModuleWindow.
        List<IModule> modules = new List<IModule>();
        if (headless)
        {
            modules.AddRange(UsergameDemo.Modules());
        }
        else
        {
            ModulePlatformDesktop platform = new ModulePlatformDesktop();
            ModuleWindow moduleWindow = new ModuleWindow(platform);
            modules.Add(platform);
            modules.Add(moduleWindow);
            modules.AddRange(UsergameDemo.ModulesWindowed(moduleWindow));
        }
        // Info, not Trace: the Rust log crate is process-global, so winit's internal Debug/Trace records (wayland globals, calloop dispatch) flow through the engine pipeline too -- one pipeline is the design, the level filter is the knob.
        Engine.Initialize(new EngineConfig { LogLevelMax = (int)LogLevel.Info, LogBufferCapacity = 1024, LogStderrLevelMax = (int)LogLevel.Warn }, (level, message) => Console.WriteLine($"[{level}] {message}"), modules);
        while (!Engine.ExitQueued)
        {
            MainLoop.Iterate();
            if (!headless)
            {
                // Heartbeat pacing stays host-side (MainLoop converts elapsed to steps, it never blocks): the platform pump is non-blocking, so an unslept loop is a 100%-CPU spin. Honest placeholder until render pacing/vsync exists (M6). Headless runs unslept -- the smoke row spins real time to tick 100 (~1.7s) and exits.
                System.Threading.Thread.Sleep(10);
            }
        }
        Console.WriteLine($"Buckminster.Host.Desktop: usergame finished after {Engine.TickCount} ticks, shutting down clean");
        // The machine-shaped sentinel the test-all smoke row matches exactly (BUCK-TEST-EXIT lineage); the human-readable line above is free to change. Exact at 100 because MainLoop stops a tick burst at ExitQueued -- a CI stall around tick 100 cannot overshoot.
        Console.WriteLine($"BUCK-DEMO-EXIT ticks={Engine.TickCount}");
        Engine.Shutdown();
        return 0;
    }
}
