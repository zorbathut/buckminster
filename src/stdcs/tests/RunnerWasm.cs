using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework.Api;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

namespace Buckminster.Tests;

// The in-host test runner for the wasm targets: the real NUnit engine (NUnitTestAssemblyRunner, shipped inside nunit.framework itself) run in-process, so the wasm cells get the same test semantics as `dotnet test` -- [SetUp], [TestCase], [Values], Assert.Multiple, the works. Two package settings make the engine single-thread-safe: RunOnMainThread (work items execute synchronously on the calling thread) and SynchronousEvents (listener events delivered inline instead of via the EventPump thread, whose Thread.Start throws PlatformNotSupportedException on wasm). Approach proven in-browser by planefarer6's WebRun.cs.
//
// Known hazards, all loud: a genuinely-async test (one that actually yields to the JS event loop) deadlocks under RunOnMainThread and is killed by the harness timeouts (tools/test-all.py for the node cell, the CDP driver for the browser cell); [RequiresThread]/apartment tests throw PlatformNotSupportedException. And a ceiling: the browser main thread is blocked for the whole synchronous run, and the CDP driver's per-command timeout is 30s (wasmbrowser.py _ChromePipe.command) -- a suite exceeding ~30s of wasm execution starts failing the web cell. Escalation path when the suite grows: per-fixture JSExport calls chained via setTimeout (planefarer's chunking, adapted).
internal static class RunnerWasm
{
    // Trimming a fixture away would silently shrink the suite, so callers must root the test assembly (WasmHost.props sets TrimmerRootAssembly) and acknowledge with a suppression.
    [RequiresUnreferencedCode("NUnit discovery reflects over the whole test assembly; the caller must root it against trimming (TrimmerRootAssembly)")]
    internal static (int Failures, string Report) Run(Assembly assembly)
    {
        NUnitTestAssemblyRunner runner = new NUnitTestAssemblyRunner(new DefaultTestAssemblyBuilder());
        Dictionary<string, object> settings = new Dictionary<string, object>
        {
            { NUnit.FrameworkPackageSettings.RunOnMainThread, true },
            { NUnit.FrameworkPackageSettings.SynchronousEvents, true },
        };
        // Loading the Assembly directly is fine despite assembly.Location being empty on wasm (bundled assemblies aren't files): NUnit 4.6 falls back to the assembly name, so planefarer's load-by-name-string workaround for its 4.2-era failure is obsolete.
        ITest loaded = runner.Load(assembly, settings);
        if (loaded.RunState != RunState.Runnable)
        {
            // Discovery failures don't throw: DefaultTestAssemblyBuilder catches everything and returns an invalid suite, parking the real exception text in the skip-reason property.
            object? reason = loaded.Properties.Get(PropertyNames.SkipReason);
            throw new InvalidOperationException($"NUnit test discovery failed for {assembly.GetName().Name} ({loaded.RunState}): {reason ?? "(no reason recorded)"}");
        }
        int expected = runner.CountTestCases(TestFilter.Empty);
        if (expected == 0)
        {
            // Zero tests means the suite didn't reach this assembly (broken source links, glob drift) -- reporting success would be a silent shrink.
            throw new InvalidOperationException($"NUnit found no tests in {assembly.GetName().Name} -- the test sources didn't make it into this assembly.");
        }
        // NUnit redirects Console into per-test capture for the run's duration; grab the real writer first so the listener can stream progress past the capture. A wasm-level trap (this codebase deliberately pokes panic/FFI machinery) kills everything with no C# catch possible, and the streamed START line is then the only attribution of which test died. Reaches the node terminal; the browser driver reads only the DOM, so the web cell gains nothing from it.
        TextWriter realOut = Console.Out;
        Listener listener = new Listener(realOut);
        ITestResult result = runner.Run(listener, TestFilter.Empty);
        StringBuilder report = listener.Report;
        int failures = result.FailCount;
        if (result.TotalCount != expected)
        {
            // TotalCount is NUnit's own five-bucket sum (pass/fail/skip/inconclusive/warning). A shortfall means cases were silently dropped -- fail loud instead of reporting a smaller-but-green run.
            failures += 1;
            report.AppendLine($"CROSS-CHECK FAILED: executed {result.TotalCount} cases but discovery counted {expected} -- some tests were silently dropped.");
        }
        if (failures == 0 && result.ResultState.Status == TestStatus.Failed)
        {
            // A suite-level failure with zero failing cases (e.g. a throwing [OneTimeTearDown]) would otherwise vanish: the listener reports only leaf cases and FailCount stays 0. Native dotnet test is green on this shape (an adapter blind spot) -- here loud beats parity.
            failures += 1;
            report.AppendLine($"SUITE-LEVEL FAILURE: {result.Message}");
        }
        report.AppendLine($"total: {result.TotalCount}, passed: {result.PassCount}, failed: {result.FailCount}, skipped: {result.SkipCount}, inconclusive: {result.InconclusiveCount}, warnings: {result.WarningCount}");
        return (failures, report.ToString());
    }

    // Builds the report as results stream in; skips and inconclusives get named lines (not just counts) so an [Ignore]d test stays visible instead of silently rotting.
    private class Listener : ITestListener
    {
        public readonly StringBuilder Report = new StringBuilder();
        private readonly TextWriter realOut;

        public Listener(TextWriter realOut)
        {
            this.realOut = realOut;
        }

        public void TestStarted(ITest test)
        {
            if (!test.IsSuite)
            {
                realOut.WriteLine($"START {test.FullName}");
            }
        }

        public void TestFinished(ITestResult result)
        {
            if (result.Test.IsSuite)
            {
                return;
            }
            switch (result.ResultState.Status)
            {
                case TestStatus.Passed:
                    Report.AppendLine($"PASS {result.FullName}");
                    break;
                case TestStatus.Failed:
                    Report.AppendLine($"FAIL {result.FullName}");
                    Report.AppendLine($"    {result.Message}");
                    if (!string.IsNullOrEmpty(result.StackTrace))
                    {
                        Report.AppendLine($"    {result.StackTrace}");
                    }
                    if (!string.IsNullOrEmpty(result.Output))
                    {
                        // The test's captured Console output -- without this a failing test's diagnostics evaporate.
                        Report.AppendLine($"    output: {result.Output}");
                    }
                    break;
                case TestStatus.Skipped:
                    Report.AppendLine($"SKIP {result.FullName} ({result.Message})");
                    break;
                case TestStatus.Inconclusive:
                    Report.AppendLine($"INCONCLUSIVE {result.FullName}");
                    break;
                case TestStatus.Warning:
                    Report.AppendLine($"WARN {result.FullName} ({result.Message})");
                    break;
                default:
                    throw new NotSupportedException($"unknown NUnit test status {result.ResultState.Status} from {result.FullName}");
            }
        }

        public void TestOutput(TestOutput output)
        {
        }

        public void SendMessage(TestMessage message)
        {
        }
    }
}
