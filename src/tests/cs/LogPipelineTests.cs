using System;
using System.Collections.Generic;
using Buckminster;
using Buckminster.Ffi;
using NUnit.Framework;

namespace Buckminster.Tests;

// The unified log pipeline: C# and Rust records share one buffer and one delivery path, and the exit-drain at the tail of every buck_* call delivers records synchronously within the emitting call -- causal C# stacks, records-before-result on failures. Log.* is the C# entry point and round-trips through Rust (buck_log), so these tests exercise the real pipeline end to end.
[TestFixture]
public class LogPipelineTests
{
    private static EngineScope CreateEngine(Action<LogLevel, string> logSink, int levelMax = 5, uint capacity = 1024)
    {
        // Stderr echo off in tests: the echo channel is verified by eye via the desktop host (and cheaply capturable in the chunk-6 two-process harness); asserting our own process stderr isn't worth the contortion.
        return new EngineScope(logSink, config: new EngineConfig { LogLevelMax = levelMax, LogBufferCapacity = capacity, LogStderrLevelMax = 0 });
    }

    [Test]
    public void DeliveryIsSynchronousWithinTheEmittingCall()
    {
        List<(LogLevel Level, string Message)> received = new List<(LogLevel, string)>();
        using EngineScope engine = CreateEngine((level, message) => received.Add((level, message)));
        Log.Info("first");
        // The record arrived before Log.Info returned -- delivered by that call's own exit-drain, not some later pump.
        Assert.That(received, Is.EqualTo(new[] { (LogLevel.Info, "first") }));
        Log.Warn("second");
        Assert.That(received, Is.EqualTo(new[] { (LogLevel.Info, "first"), (LogLevel.Warn, "second") }));
    }

    [Test]
    public void RecordsDeliverBeforeTheFailureResultInOrder()
    {
        List<string> received = new List<string>();
        using EngineScope engine = CreateEngine((level, message) => received.Add(message));
        Assert.That(NativeMethods.buck_engine_create(new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 16, LogStderrLevelMax = 0 }, out ulong rawEngine), Is.EqualTo(FfiCode.Ok));
        // The probe logs two records then panics: both must reach the log sink, in emission order, BEFORE the Panic code comes back -- in-order reporting is the hard guarantee, and a failed call's diagnostics deserve delivery MORE, not less.
        FfiCode code = NativeMethods.buck_engine_test_log_then_panic(rawEngine);
        Assert.That(code, Is.EqualTo(FfiCode.Panic));
        Assert.That(received, Is.EqualTo(new[] { "first record before the panic", "second record before the panic" }));
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("deliberate panic after logging"));
        // And the panic still poisoned the engine (delivery is orthogonal to the poison policy).
        Assert.That(NativeMethods.buck_engine_tick(rawEngine, 0.016, out _), Is.EqualTo(FfiCode.EnginePoisoned));
        Assert.That(NativeMethods.buck_engine_destroy(rawEngine), Is.EqualTo(FfiCode.Ok));
    }

    [Test]
    public void LastErrorSurvivesASinkThatReentersTheFfi()
    {
        // The write-last sequencing rule made distinguishable: a sink that re-enters buck_* during a failing call's drain runs a nested guard whose own exit updates the thread's last-error state -- so the outer failure's message survives ONLY because it is stored after the drain. An implementation storing it before the drain passes every other test in this fixture.
        List<FfiCode> nestedCodes = new List<FfiCode>();
        using EngineScope engine = CreateEngine((level, message) => nestedCodes.Add(NativeMethods.buck_add(1, 2, out _)));
        Assert.That(NativeMethods.buck_engine_create(new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 16, LogStderrLevelMax = 0 }, out ulong rawEngine), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_engine_test_log_then_panic(rawEngine), Is.EqualTo(FfiCode.Panic));
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("deliberate panic after logging"));
        // Journal-style check that the nested calls really ran and succeeded (asserting inside the sink would surface as CallbackError and muddy the path under test).
        Assert.That(nestedCodes, Is.Not.Empty);
        Assert.That(nestedCodes, Is.All.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_engine_destroy(rawEngine), Is.EqualTo(FfiCode.Ok));
    }

    [Test]
    public void ReentrantSinkNeitherRecursesNorLosesRecords()
    {
        List<string> received = new List<string>();
        bool nested = false;
        using EngineScope engine = CreateEngine((level, message) =>
        {
            received.Add(message);
            if (!nested)
            {
                nested = true;
                // A sink that logs: the nested buck_log's own exit-drain must skip (draining flag), the record waits for the next exit -- no recursion, no loss.
                Log.Info("from inside the sink");
            }
        });
        Log.Info("outer");
        Assert.That(received, Is.EqualTo(new[] { "outer" }));
        Log.Info("next");
        Assert.That(received, Is.EqualTo(new[] { "outer", "from inside the sink", "next" }));
    }

    [Test]
    public void LevelFilterDropsBelowThreshold()
    {
        List<LogLevel> received = new List<LogLevel>();
        using EngineScope engine = CreateEngine((level, message) => received.Add(level), levelMax: (int)LogLevel.Warn);
        Log.Error("kept");
        Log.Warn("kept");
        Log.Info("filtered");
        Log.Trace("filtered");
        Assert.That(received, Is.EqualTo(new[] { LogLevel.Error, LogLevel.Warn }));
    }

    [Test]
    public void OverflowDropsOldestAndReportsTheLossLoudly()
    {
        // Overflow requires records to accumulate without a drain between them; the natural accumulation point under exit-drain is nested emission (a sink that logs runs under the draining flag, so its records buffer for the next exit).
        List<(LogLevel Level, string Message)> received = new List<(LogLevel, string)>();
        using EngineScope engine = CreateEngine((level, message) =>
        {
            received.Add((level, message));
            if (message == "outer")
            {
                Log.Info("nested-one");
                Log.Info("nested-two");
                Log.Info("nested-three");
            }
        }, capacity: 2);
        // The three nested records buffer under capacity 2: "nested-one" is dropped-oldest. "final" then pushes "nested-two" out as well before its exit-drain runs.
        Log.Info("outer");
        Log.Info("final");
        Assert.That(received, Has.Count.EqualTo(4));
        Assert.That(received[0].Message, Is.EqualTo("outer"));
        Assert.That(received[1].Level, Is.EqualTo(LogLevel.Warn));
        Assert.That(received[1].Message, Does.Contain("2 log record"));
        Assert.That(received[1].Message, Does.Contain("never reached the log sink"));
        Assert.That(received[2].Message, Is.EqualTo("nested-three"));
        Assert.That(received[3].Message, Is.EqualTo("final"));
    }

    [Test]
    public void ThrowingSinkSurfacesAtTheCausalCallAndTheTailRedelivers()
    {
        List<string> received = new List<string>();
        bool sinkHealthy = true;
        using EngineScope engine = CreateEngine((level, message) =>
        {
            if (!sinkHealthy && message == "boom")
            {
                throw new InvalidOperationException("sink deliberately failing");
            }
            received.Add(message);
        });
        sinkHealthy = false;
        Log.Info("before");
        // "boom"'s own Log.Info throws -- the causal call site, not a later pump. The record is lost with this exception as its marker.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Log.Info("boom"))!;
        Assert.That(error.Message, Does.Contain("sink deliberately failing"));
        Assert.That(received, Is.EqualTo(new[] { "before" }));
        sinkHealthy = true;
        Log.Info("after");
        Assert.That(received, Is.EqualTo(new[] { "before", "after" }));
    }

    [Test]
    public void DisposeDeliversTheStrandedTail()
    {
        List<string> received = new List<string>();
        bool sinkHealthy = true;
        EngineScope engine = CreateEngine((level, message) =>
        {
            if (!sinkHealthy)
            {
                throw new InvalidOperationException("sink down");
            }
            received.Add(message);
        });
        // Stranding needs a multi-record batch whose sink fails partway (a single Log call's batch is one record -- its failure leaves no tail). The two-record probe delivers exactly that: sink fails on record one (lost), record two is pushed back stranded.
        Assert.That(NativeMethods.buck_engine_create(new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 16, LogStderrLevelMax = 0 }, out ulong rawEngine), Is.EqualTo(FfiCode.Ok));
        sinkHealthy = false;
        Assert.That(NativeMethods.buck_engine_test_log_then_panic(rawEngine), Is.EqualTo(FfiCode.Panic));
        // The sink's exception is stashed (the body's Panic won the return code); take it so it can't misattribute to a later call.
        Assert.That(CallbackExceptionStash.Take(), Is.Not.Null);
        sinkHealthy = true;
        // Disposing the scope (Engine.Shutdown) destroys first; destroy's own exit-drain delivers the stranded tail through the still-registered sink. The raw probe engine is destroyed after (its exit sees a cleared sink and delivers nothing).
        engine.Dispose();
        Assert.That(received, Is.EqualTo(new[] { "second record before the panic" }));
        Assert.That(NativeMethods.buck_engine_destroy(rawEngine), Is.EqualTo(FfiCode.Ok));
    }

    [Test]
    public void DropReportSurvivesAThrowingSink()
    {
        // The one record whose loss would defeat the loudness invariant: if the sink throws on the synthesized drop-report itself, the count must persist and be re-reported (with any accrued additions) once delivery works.
        List<string> received = new List<string>();
        bool reportBlocked = true;
        using EngineScope engine = CreateEngine((level, message) =>
        {
            if (reportBlocked && message.Contains("never reached the log sink"))
            {
                throw new InvalidOperationException("report rejected");
            }
            received.Add(message);
            if (message == "outer")
            {
                Log.Info("nested-one");
                Log.Info("nested-two");
                Log.Info("nested-three");
            }
        }, capacity: 2);
        Log.Info("outer");
        // nested-one was dropped (capacity 2); "mid" evicts nested-two; the drain leads with the report, which the sink rejects -- report lost from the batch but its count persists, tail pushed back.
        Assert.Throws<InvalidOperationException>(() => Log.Info("mid"));
        reportBlocked = false;
        // "post" evicts nested-three (count now 3); this drain's report must claim all three.
        Log.Info("post");
        Assert.That(received, Is.EqualTo(new[] { "outer", "3 log record(s) never reached the log sink: buffer capacity exceeded (stderr may have carried them)", "mid", "post" }));
    }

    [Test]
    public void TailRedeliversAheadOfFreshRecords()
    {
        List<string> received = new List<string>();
        bool sinkHealthy = true;
        using EngineScope engine = CreateEngine((level, message) =>
        {
            if (!sinkHealthy)
            {
                throw new InvalidOperationException("sink down");
            }
            received.Add(message);
        });
        Assert.That(NativeMethods.buck_engine_create(new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 16, LogStderrLevelMax = 0 }, out ulong rawEngine), Is.EqualTo(FfiCode.Ok));
        sinkHealthy = false;
        // Probe batch of two: sink fails on record one (lost), record two is the pushed-back tail.
        Assert.That(NativeMethods.buck_engine_test_log_then_panic(rawEngine), Is.EqualTo(FfiCode.Panic));
        Assert.That(CallbackExceptionStash.Take(), Is.Not.Null);
        sinkHealthy = true;
        // One batch, two records: the redelivered tail must precede the fresh record -- it is chronologically older.
        Log.Info("fresh");
        Assert.That(received, Is.EqualTo(new[] { "second record before the panic", "fresh" }));
        Assert.That(NativeMethods.buck_engine_destroy(rawEngine), Is.EqualTo(FfiCode.Ok));
    }

    [Test]
    public void CreateFailureCleansUpTheSinkRegistration()
    {
        // The regression test for the create-failure path: a new sink that throws on buffered residue during registration must leave NO dangling Rust-side registration -- reversed cleanup order once left a dead key installed, breaking every subsequent create in the process.
        List<string> received = new List<string>();
        bool sinkHealthy = true;
        EngineScope first = CreateEngine((level, message) =>
        {
            if (!sinkHealthy)
            {
                throw new InvalidOperationException("first sink down");
            }
        });
        // Three fresh raw engines (the probe poisons its engine, so each round needs its own), created while the sink is healthy and the buffer empty.
        EngineConfig rawConfig = new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 16, LogStderrLevelMax = 0 };
        ulong[] rawEngines = new ulong[3];
        for (int i = 0; i < rawEngines.Length; i++)
        {
            Assert.That(NativeMethods.buck_engine_create(rawConfig, out rawEngines[i]), Is.EqualTo(FfiCode.Ok));
        }
        sinkHealthy = false;
        // Three probe rounds: each drain loses exactly one record to the down sink and pushes the rest back, netting a growing tail. After round three the tail is three records; shutting down (loses one) leaves two -- enough residue to survive the rejecting sink below (which loses one more) and still prove delivery to the healthy create.
        foreach (ulong rawEngine in rawEngines)
        {
            Assert.That(NativeMethods.buck_engine_test_log_then_panic(rawEngine), Is.EqualTo(FfiCode.Panic));
            Assert.That(CallbackExceptionStash.Take(), Is.Not.Null);
        }
        Assert.Throws<InvalidOperationException>(first.Dispose);
        // Residue is in the buffer, no sink registered. A create whose sink rejects the residue must fail cleanly...
        Assert.Throws<InvalidOperationException>(() => new EngineScope((level, message) => throw new InvalidOperationException("rejects residue"), config: new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 16, LogStderrLevelMax = 0 }));
        // ...and the process must remain fully usable: a healthy create succeeds and receives the residue at registration.
        using EngineScope second = new EngineScope((level, message) => received.Add(message), config: new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 16, LogStderrLevelMax = 0 });
        Assert.That(received, Does.Contain("second record before the panic"));
        foreach (ulong rawEngine in rawEngines)
        {
            Assert.That(NativeMethods.buck_engine_destroy(rawEngine), Is.EqualTo(FfiCode.Ok));
        }
    }

    [Test]
    public void ExLogsTheFullExceptionAtErrorLevel()
    {
        List<(LogLevel Level, string Message)> received = new List<(LogLevel, string)>();
        using EngineScope engine = CreateEngine((level, message) => received.Add((level, message)));
        Log.Ex(new InvalidOperationException("the thing broke"));
        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(received[0].Level, Is.EqualTo(LogLevel.Error));
        Assert.That(received[0].Message, Does.Contain("InvalidOperationException"));
        Assert.That(received[0].Message, Does.Contain("the thing broke"));
    }

    [Test]
    public void EmptyMessageIsSafe()
    {
        // C#'s fixed on an empty array pins NULL; the Rust side must handle (null, 0) without touching the pointer (from_raw_parts(null, 0) is a non-unwinding abort no guard can contain).
        List<string> received = new List<string>();
        using EngineScope engine = CreateEngine((level, message) => received.Add(message));
        Log.Info("");
        Assert.That(received, Is.EqualTo(new[] { "" }));
    }

    [Test]
    public void InvalidConfigLevelsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => CreateEngine((level, message) => { }, levelMax: 6));
        Assert.Throws<InvalidOperationException>(() => CreateEngine((level, message) => { }, levelMax: -1));
        Assert.Throws<InvalidOperationException>(() => new EngineScope((level, message) => { }, config: new EngineConfig { LogLevelMax = 5, LogBufferCapacity = 16, LogStderrLevelMax = 9 }));
    }

    // A deliberately-discarding sink: silent log dropping is banned as a DEFAULT, and writing the discard down as an implementation is the sanctioned form.
    private sealed class SinkNull : ILogSink
    {
        public void Write(LogLevel level, string msg)
        {
        }
    }

    [Test]
    public void SinkReplacementReleasesTheSupersededRegistration()
    {
        // The no-manual-bookkeeping lifetime claim, pinned: every transition (set-over-set, clear) must release exactly the superseded key via the Rust proxy's drop firing the release thunk.
        LogSinkVtable first = LogSinkThunks.Create(new SinkNull());
        Native.LogSinkSet(in first);
        LogSinkVtable second = LogSinkThunks.Create(new SinkNull());
        Native.LogSinkSet(in second);
        Assert.That(() => CallbackTable.Get(first.Userdata), Throws.TypeOf<System.Collections.Generic.KeyNotFoundException>());
        Assert.That(CallbackTable.Get(second.Userdata), Is.Not.Null);
        Native.LogSinkClear();
        Assert.That(() => CallbackTable.Get(second.Userdata), Throws.TypeOf<System.Collections.Generic.KeyNotFoundException>());
    }
}
