using System.Collections.Generic;
using NUnit.Framework;
using Buckminster.Usergame.Demo;

namespace Buckminster.Tests;

// The demo usergame end-to-end, in-process: the same boot-list/MainLoop cycle the desktop host runs (driven through the deterministic IterateWithElapsed instead of the wall clock), so the wasm cells get demo AND pacing coverage the native-only smoke row can't give them.
[TestFixture]
public class UsergameDemoTests
{
    [Test]
    public void DemoRunsToExactlyOneHundredTicks()
    {
        using EngineScope scope = new EngineScope(modules: UsergameDemo.Modules());
        // One fixed step of elapsed per iterate: the first latches Ready (no ticks), each later one runs exactly one. Runaway bound well above 100 so "never exited" reads as its own failure, not an off-by-one.
        int iterates = 0;
        while (!Engine.ExitQueued && iterates < 1000)
        {
            MainLoop.IterateWithElapsed(MainLoop.FixedStep);
            iterates += 1;
        }
        Assert.That(Engine.ExitQueued, Is.True, $"ExitQueued never set after {Engine.TickCount} ticks");
        Assert.That(Engine.TickCount, Is.EqualTo(100));
    }
}
