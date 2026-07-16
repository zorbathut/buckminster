using System;

namespace Buckminster.Usergame.Demo;

// The windowed demo: creates "a window" (the four-part discussions' phrase -- one explicit window, also the future WebGPU render target), logs every event kind it receives (the M5 observability proof: events provably reach C#), and ends the run on Escape or the close button. The ending models the full cooperative-close round trip: destroy the window explicitly, THEN queue exit -- windows not destroyed before process exit are torn down by the OS silently (ARCHITECTURE's shutdown contract), and the demo demonstrates the right behavior instead.
public sealed class ModuleDemoWindowed : IModule
{
    private readonly ModuleWindow moduleWindow;

    public ModuleDemoWindowed(ModuleWindow moduleWindow)
    {
        this.moduleWindow = moduleWindow;
    }

    public Type[] Dependencies
    {
        get { return new[] { typeof(ModuleWindow) }; }
    }

    public void Initialize(Engine engine)
    {
        // Local, not a field: ModuleWindow keeps the object alive and the handlers receive it as their parameter.
        Window window = moduleWindow.CreateWindow("Buckminster demo -- Escape or close button quits", 640, 360);
        Log.Info($"ModuleDemoWindowed: window {window.Id} created at {window.Width}x{window.Height}");
        window.Resized += (w, width, height) => Log.Info($"ModuleDemoWindowed: resized to {width}x{height}");
        window.FocusChanged += (w, focused) => Log.Info($"ModuleDemoWindowed: focus {(focused ? "gained" : "lost")}");
        window.CloseRequested += w =>
        {
            Log.Info("ModuleDemoWindowed: close requested");
            Finish(w, engine);
        };
        window.Key += (w, key) =>
        {
            Log.Info($"ModuleDemoWindowed: key {key.Code} {(key.Pressed ? "down" : "up")}{(key.Repeat ? " (repeat)" : "")} modifiers={key.Modifiers}");
            if (key.Code == KeyCode.Escape && key.Pressed)
            {
                Finish(w, engine);
            }
        };
    }

    public void PumpEvents(Engine engine)
    {
    }

    public void Tick(Engine engine, double dt)
    {
    }

    private static void Finish(Window window, Engine engine)
    {
        Log.Info("ModuleDemoWindowed: shutting down");
        window.Destroy();
        engine.QueueExit();
    }
}
