using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Buckminster.Tests;

// ModuleWindow with no platform module: windows are virtual (no native backing) and events arrive only by injection -- which is exactly what makes the event plumbing testable headlessly on every cell, and what the future recorded-input trace injects through. Window.Id (per-module creation-order index) is the identity events name a window by; native RIDs are a platform detail virtual windows don't have.
[TestFixture]
public class ModuleWindowTests
{
    [Test]
    public void VirtualWindowCarriesTitleSizeAndCreationOrderIds()
    {
        ModuleWindow module = new ModuleWindow();
        Window first = module.CreateWindow("first", 320, 240);
        Window second = module.CreateWindow("second", 640, 480);
        Assert.That(first.Id, Is.EqualTo(1u));
        Assert.That(second.Id, Is.EqualTo(2u));
        Assert.That(first.Title, Is.EqualTo("first"));
        Assert.That(first.Width, Is.EqualTo(320u));
        Assert.That(first.Height, Is.EqualTo(240u));
    }

    [Test]
    public void InjectedEventsFireThePerWindowEvents()
    {
        ModuleWindow module = new ModuleWindow();
        Window window = module.CreateWindow("w", 320, 240);
        List<string> journal = new List<string>();
        window.Resized += (w, width, height) =>
        {
            // The contract a handler relies on: the window's own size has ALREADY updated when the event fires.
            Assert.That(w.Width, Is.EqualTo(width));
            Assert.That(w.Height, Is.EqualTo(height));
            journal.Add($"resized:{width}x{height}");
        };
        window.CloseRequested += w => journal.Add("close-requested");
        window.Key += (w, key) => journal.Add($"key:{key.Code}:{(key.Pressed ? "down" : "up")}:{(key.Repeat ? "repeat" : "fresh")}:{(int)key.Modifiers}");
        window.FocusChanged += (w, focused) => journal.Add($"focus:{focused}");

        module.InjectEvent(window, WindowEventData.Resized(800, 600));
        Assert.That(window.Width, Is.EqualTo(800u));
        Assert.That(window.Height, Is.EqualTo(600u));
        module.InjectEvent(window, WindowEventData.Key(KeyCode.Escape, pressed: true, repeat: false, KeyModifiers.Control));
        module.InjectEvent(window, WindowEventData.FocusChanged(true));
        module.InjectEvent(window, WindowEventData.CloseRequested());
        Assert.That(journal, Is.EqualTo(new[] { "resized:800x600", "key:Escape:down:fresh:2", "focus:True", "close-requested" }));
    }

    [Test]
    public void DestroyFromInsideACloseRequestedHandlerWorks()
    {
        // The single most natural consumer line -- window.CloseRequested += destroy -- must work, not be prohibited in prose.
        ModuleWindow module = new ModuleWindow();
        Window window = module.CreateWindow("w", 320, 240);
        window.CloseRequested += w => w.Destroy();
        module.InjectEvent(window, WindowEventData.CloseRequested());
        Assert.That(window.IsDestroyed, Is.True);
    }

    [Test]
    public void DestroyIsIdempotentAndInjectingIntoADestroyedWindowThrows()
    {
        ModuleWindow module = new ModuleWindow();
        Window window = module.CreateWindow("w", 320, 240);
        window.Destroy();
        window.Destroy();
        Assert.That(window.IsDestroyed, Is.True);
        // Injecting into a destroyed window is a caller bug (the platform producer resolves-and-drops unknown windows BEFORE injecting, so it can never hit this).
        Assert.Throws<InvalidOperationException>(() => module.InjectEvent(window, WindowEventData.CloseRequested()));
    }

    [Test]
    public void DependenciesFollowThePlatformWiring()
    {
        // Constructing ModulePlatformDesktop is FFI-free (the event loop is lazy), so this runs on every cell; nothing here Initializes the platform module, which WOULD touch the FFI. The registry's topo behavior itself is pinned by ModuleRegistryTests.
        ModuleWindow headless = new ModuleWindow();
        Assert.That(headless.Dependencies, Is.Empty);
        ModulePlatformDesktop platform = new ModulePlatformDesktop();
        ModuleWindow windowed = new ModuleWindow(platform);
        Assert.That(windowed.Dependencies, Is.EqualTo(new[] { typeof(ModulePlatformDesktop) }));
    }

    [Test]
    public void ModuleWindowRegistersAndPumpsHeadlessly()
    {
        // As an engine module with no platform, PumpEvents is a no-op -- the composition the smoke row and every wasm cell run.
        using Engine engine = Engine.Create(new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 1024, LogStderrLevelMax = 0 }, (level, message) => { });
        ModuleWindow module = new ModuleWindow();
        engine.RegisterModule(module);
        engine.PumpEvents();
        engine.PumpEvents();
        engine.Tick(0.016);
        Assert.That(engine.TickCount, Is.EqualTo(1ul));
    }
}
