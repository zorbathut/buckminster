using System;
using System.Collections.Generic;

namespace Buckminster.Tests;

// Per-test globals cycle: Initialize in the constructor, Shutdown on dispose. Initialize/Shutdown cyclability (pinned by EngineGlobalsTests) is what makes per-test cycles safe, and NUnit runs this suite sequentially (no [Parallelizable] anywhere), so cycles never overlap. The defaults are principled absences: null sink = written-down discard, null modules = none, null config = the test-standard config (stderr echo off -- the echo channel is verified by eye via the desktop host).
internal sealed class EngineScope : IDisposable
{
    public EngineScope(Action<LogLevel, string>? logSink = null, IReadOnlyList<IModule>? modules = null, EngineConfig? config = null)
    {
        Engine.Initialize(config ?? new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 1024, LogStderrLevelMax = 0 }, logSink ?? ((level, message) => { }), modules ?? Array.Empty<IModule>());
    }

    public void Dispose()
    {
        Engine.Shutdown();
    }
}
