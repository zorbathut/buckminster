using System;
using System.Diagnostics;

namespace Buckminster;

// The core-provided frame composition (M5.75; PLAN.md tick-as-callee refined): hosts have no interface -- each host drives Iterate() at whatever cadence its platform gives it (an owned loop on desktop, rAF on web, a display-link callback on mobile), and PACING LIVES HERE, not in hosts. Iterate turns real elapsed time into 0..N fixed-step ticks plus one render; the primitives (Engine.PumpEvents/Tick/Render) stay public as the granular seam for tests and the future replay harness. Heartbeat pacing (sleeping between iterates) remains the host's: this class converts elapsed to steps, it does not block.
public static class MainLoop
{
    // The fixed sim step Iterate feeds to Engine.Tick. Reset to the default by Engine.Initialize (it is cycle state like everything else); a host wanting a different step sets it after Initialize, before the first Iterate. Guarded because a nonpositive step would silently corrupt the accumulator math into zero-ticks-forever.
    public static double FixedStep
    {
        get
        {
            return fixedStep;
        }
        set
        {
            if (!(value > 0.0))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "FixedStep must be positive");
            }
            fixedStep = value;
        }
    }

    private static double fixedStep = FixedStepDefault;

    private const double FixedStepDefault = 1.0 / 60.0;

    // The spiral-of-death clamp (Godot max_physics_steps lineage): one iterate runs at most this many fixed steps, and the un-run excess is dropped -- a stall converts into slowed sim time, never an unbounded catch-up burst.
    internal const int MaxStepsPerIterate = 8;

    private static readonly Stopwatch clock = new Stopwatch();
    private static double lastIterateSeconds;
    private static double accumulator;

    // The host's heartbeat: reads the monotonic clock and runs one frame. Wall-clock-free callers (tests) use IterateWithElapsed.
    public static void Iterate()
    {
        if (!clock.IsRunning)
        {
            clock.Start();
        }
        double now = clock.Elapsed.TotalSeconds;
        double elapsed = now - lastIterateSeconds;
        lastIterateSeconds = now;
        IterateCore(elapsed);
    }

    // The deterministic core, exposed for tests (InternalsVisibleTo): identical to Iterate minus the clock read.
    internal static void IterateWithElapsed(double elapsedSeconds)
    {
        IterateCore(elapsedSeconds);
    }

    private static void IterateCore(double elapsed)
    {
        bool wasReady = Engine.IsReady;
        Engine.PumpEvents();
        if (!Engine.IsReady)
        {
            // Still initializing (async-shaped init: pump until Ready): nothing to pace, nothing to render.
            return;
        }
        if (!wasReady)
        {
            // The Ready latch: sim tick 0 is pinned to Ready (PLAN.md determinism), so elapsed init time must never convert into a tick burst -- the accumulator starts HERE. Deliberately no Render either: this iterate is lifecycle bookkeeping, and the first real frame (ticked, then rendered) is one heartbeat away.
            accumulator = 0.0;
            return;
        }
        accumulator += elapsed;
        // Clamp BEFORE dividing: it bounds the ratio to MaxStepsPerIterate, so the int cast can never overflow no matter how absurd the elapsed value was.
        int steps;
        if (accumulator >= FixedStep * MaxStepsPerIterate)
        {
            steps = MaxStepsPerIterate;
            // Clamped: drop the excess entirely (see MaxStepsPerIterate).
            accumulator = 0.0;
        }
        else
        {
            steps = (int)(accumulator / FixedStep);
            accumulator -= steps * FixedStep;
        }
        for (int i = 0; i < steps; i++)
        {
            if (Engine.ExitQueued)
            {
                // A queued exit ends the burst mid-iterate: the host's loop condition is about to see the flag, and ticking past the exit point would (among other things) make the exact-tick-count smoke sentinel timing-dependent.
                break;
            }
            Engine.Tick(FixedStep);
        }
        Engine.Render();
    }

    // Cycle-state reset, called by Engine.Initialize.
    internal static void ResetPacing()
    {
        FixedStep = FixedStepDefault;
        clock.Reset();
        lastIterateSeconds = 0.0;
        accumulator = 0.0;
    }
}
