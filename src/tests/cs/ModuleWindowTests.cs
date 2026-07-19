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

    // A fake platform provider for the seam (the real one lives in the host-desktop assembly, which tests can't reference -- it would drag pinvoke-bearing code into the wasm publishes, the exact thing the hoist removed). These tests only exercise wiring, never a live platform.
    private sealed class PlatformFake : IPlatformWindowing
    {
        private ulong nextRid = 1;

        public void Pump()
        {
        }

        public (uint Written, uint Remaining) Poll(Span<Buckminster.Ffi.PlatformEventRaw> buffer)
        {
            return (0, 0);
        }

        public ulong CreateNativeWindow(string title, uint width, uint height)
        {
            // Distinct nonzero rids: nonzero marks the window native-backed, distinct keeps ModuleWindow's byNativeRid map from a confusing duplicate-key blowup if a test ever creates two.
            return nextRid++;
        }

        public void DestroyNativeWindow(ulong window)
        {
        }

        public void SetNativeWindowTitle(ulong window, string title)
        {
        }
    }

    [Test]
    public void DependenciesFollowThePlatformWiring()
    {
        // The registry's topo behavior itself is pinned by ModuleRegistryTests; this pins that the wiring drives the dependency declaration -- by the provider's concrete type.
        ModuleWindow headless = new ModuleWindow();
        Assert.That(headless.Dependencies, Is.Empty);
        ModuleWindow windowed = new ModuleWindow(new PlatformFake());
        Assert.That(windowed.Dependencies, Is.EqualTo(new[] { typeof(PlatformFake) }));
    }

    [Test]
    public void ShutdownDestroysRemainingLiveWindows()
    {
        // An exit queued by anything other than a cooperative close leaves live windows; module teardown must destroy them (native windows would otherwise leak until process exit -- virtual ones prove the walk).
        ModuleWindow module = new ModuleWindow();
        Window destroyed;
        Window survivorOne;
        Window survivorTwo;
        using (EngineScope scope = new EngineScope(modules: new IModule[] { module }))
        {
            Engine.PumpEvents();
            destroyed = module.CreateWindow("already gone", 320, 240);
            survivorOne = module.CreateWindow("one", 320, 240);
            survivorTwo = module.CreateWindow("two", 320, 240);
            destroyed.Destroy();
        }
        Assert.That(survivorOne.IsDestroyed, Is.True);
        Assert.That(survivorTwo.IsDestroyed, Is.True);
        Assert.That(destroyed.IsDestroyed, Is.True);
    }

    [Test]
    public void ModuleWindowRegistersAndPumpsHeadlessly()
    {
        // As an engine module with no platform, PumpEvents is a no-op -- the composition the smoke row and every wasm cell run.
        using EngineScope scope = new EngineScope(modules: new IModule[] { new ModuleWindow() });
        Engine.PumpEvents();
        Engine.PumpEvents();
        Engine.Tick(0.016);
        Assert.That(Engine.TickCount, Is.EqualTo(1ul));
    }
}
