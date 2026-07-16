using NUnit.Framework;
using Buckminster.Usergame.Demo;

namespace Buckminster.Tests;

// The demo usergame end-to-end, in-process: the same register/pump/tick-until-ExitQueued cycle the desktop host runs, so the wasm cells get demo coverage the native-only smoke row can't give them.
[TestFixture]
public class UsergameDemoTests
{
    [Test]
    public void DemoRunsToExactlyOneHundredTicks()
    {
        using Engine engine = Engine.Create(new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 1024, LogStderrLevelMax = 0 }, (level, message) => { });
        UsergameDemo.Register(engine);
        while (!engine.IsReady)
        {
            engine.PumpEvents();
        }
        // Runaway bound well above 100 so "never exited" reads as its own failure, not an off-by-one.
        while (!engine.ExitQueued && engine.TickCount < 1000)
        {
            engine.PumpEvents();
            engine.Tick(1.0 / 60.0);
        }
        Assert.That(engine.ExitQueued, Is.True, $"ExitQueued never set after {engine.TickCount} ticks");
        Assert.That(engine.TickCount, Is.EqualTo(100));
    }
}
