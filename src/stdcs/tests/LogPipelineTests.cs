using System;
using System.Collections.Generic;
using Buckminster;
using Buckminster.Ffi;
using NUnit.Framework;

namespace Buckminster.Tests;

// The Rust-to-C# log pipeline: records buffer in Rust (a synchronous callback per log statement would violate callback rule 4 -- a log deep inside borrowed engine state must not re-enter C#) and drain to the sink at the safe point in PumpEvents. buck_test_log emits through the real log::log! path, so these tests exercise the actual pipeline.
[TestFixture]
public class LogPipelineTests
{
    private static Engine CreateEngine(Action<LogLevel, string> sink, int levelMax = 5, uint capacity = 1024)
    {
        return Engine.Create(new EngineConfig { LogLevelMax = levelMax, LogBufferCapacity = capacity }, sink);
    }

    private static void EmitLog(LogLevel level, string message)
    {
        FfiCode code = NativeMethods.TestLog((int)level, message);
        Assert.That(code, Is.EqualTo(FfiCode.Ok), NativeMethods.LastErrorMessage());
    }

    [Test]
    public void RecordsReachTheSinkInOrderAtThePump()
    {
        List<(LogLevel Level, string Message)> received = new List<(LogLevel, string)>();
        using Engine engine = CreateEngine((level, message) => received.Add((level, message)));
        engine.PumpEvents();
        received.Clear();
        EmitLog(LogLevel.Info, "first");
        EmitLog(LogLevel.Warn, "second");
        // Buffered, not synchronous: nothing may arrive before the pump.
        Assert.That(received, Is.Empty);
        engine.PumpEvents();
        Assert.That(received, Is.EqualTo(new[] { (LogLevel.Info, "first"), (LogLevel.Warn, "second") }));
    }

    [Test]
    public void LevelFilterDropsBelowThreshold()
    {
        List<LogLevel> received = new List<LogLevel>();
        using Engine engine = CreateEngine((level, message) => received.Add(level), levelMax: (int)LogLevel.Warn);
        engine.PumpEvents();
        received.Clear();
        EmitLog(LogLevel.Error, "kept");
        EmitLog(LogLevel.Warn, "kept");
        EmitLog(LogLevel.Info, "filtered");
        EmitLog(LogLevel.Trace, "filtered");
        engine.PumpEvents();
        Assert.That(received, Is.EqualTo(new[] { LogLevel.Error, LogLevel.Warn }));
    }

    [Test]
    public void OverflowDropsOldestAndReportsTheLossLoudly()
    {
        List<(LogLevel Level, string Message)> received = new List<(LogLevel, string)>();
        using Engine engine = CreateEngine((level, message) => received.Add((level, message)), capacity: 2);
        engine.PumpEvents();
        received.Clear();
        EmitLog(LogLevel.Info, "one");
        EmitLog(LogLevel.Info, "two");
        EmitLog(LogLevel.Info, "three");
        engine.PumpEvents();
        // Capacity 2: "one" was dropped-oldest, and the drain must SAY so, not just deliver a smaller batch.
        Assert.That(received, Has.Count.EqualTo(3));
        Assert.That(received[0].Level, Is.EqualTo(LogLevel.Warn));
        Assert.That(received[0].Message, Does.Contain("1 log record"));
        Assert.That(received[1].Message, Is.EqualTo("two"));
        Assert.That(received[2].Message, Is.EqualTo("three"));
    }

    [Test]
    public void ThrowingSinkSurfacesAndTheTailIsRedeliveredNextPump()
    {
        List<string> received = new List<string>();
        bool sinkHealthy = false;
        using Engine engine = CreateEngine((level, message) =>
        {
            if (!sinkHealthy && message == "boom")
            {
                throw new InvalidOperationException("sink deliberately failing");
            }
            received.Add(message);
        });
        engine.PumpEvents();
        received.Clear();
        EmitLog(LogLevel.Info, "before");
        EmitLog(LogLevel.Info, "boom");
        EmitLog(LogLevel.Info, "after1");
        EmitLog(LogLevel.Info, "after2");
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(engine.PumpEvents)!;
        Assert.That(error.Message, Does.Contain("sink deliberately failing"));
        // "before" was delivered; "boom" is lost WITH the exception as its loud marker; the undelivered tail must come back on the next pump, not vanish.
        Assert.That(received, Is.EqualTo(new[] { "before" }));
        sinkHealthy = true;
        engine.PumpEvents();
        Assert.That(received, Is.EqualTo(new[] { "before", "after1", "after2" }));
    }

    [Test]
    public void DisposeRunsAFinalDrain()
    {
        List<string> received = new List<string>();
        Engine engine = CreateEngine((level, message) => received.Add(message));
        engine.PumpEvents();
        received.Clear();
        EmitLog(LogLevel.Info, "last words");
        engine.Dispose();
        Assert.That(received, Is.EqualTo(new[] { "last words" }));
    }

    [Test]
    public void EmptyMessageIsSafe()
    {
        // C#'s fixed on an empty array pins NULL; the Rust side must handle (null, 0) without touching the pointer (from_raw_parts(null, 0) is a non-unwinding abort no guard can contain).
        List<string> received = new List<string>();
        using Engine engine = CreateEngine((level, message) => received.Add(message));
        engine.PumpEvents();
        received.Clear();
        EmitLog(LogLevel.Info, "");
        engine.PumpEvents();
        Assert.That(received, Is.EqualTo(new[] { "" }));
    }

    [Test]
    public void DropReportSurvivesAThrowingSink()
    {
        // The one record whose loss would defeat the loudness invariant: if the sink throws on the synthesized drop-report itself, the count must persist and be re-reported on the next pump.
        List<string> received = new List<string>();
        bool sinkHealthy = false;
        using Engine engine = CreateEngine((level, message) =>
        {
            if (!sinkHealthy)
            {
                throw new InvalidOperationException("sink down");
            }
            received.Add(message);
        }, capacity: 1);
        sinkHealthy = true;
        engine.PumpEvents();
        received.Clear();
        sinkHealthy = false;
        EmitLog(LogLevel.Info, "one");
        EmitLog(LogLevel.Info, "two");
        Assert.Throws<InvalidOperationException>(engine.PumpEvents);
        sinkHealthy = true;
        engine.PumpEvents();
        Assert.That(received, Has.Count.EqualTo(2));
        Assert.That(received[0], Does.Contain("1 log record"));
        Assert.That(received[1], Is.EqualTo("two"));
    }

    [Test]
    public void InvalidLevelMaxIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => CreateEngine((level, message) => { }, levelMax: 6));
        Assert.Throws<InvalidOperationException>(() => CreateEngine((level, message) => { }, levelMax: -1));
    }
}
