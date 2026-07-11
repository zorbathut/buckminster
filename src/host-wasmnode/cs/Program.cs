using System;
using Buckminster.Tests;

namespace Buckminster.Host.WasmNode;

// The Node-hosted wasm host (the wasm-desktop run target). Its M3 job is running the C# test suite in-host and carrying the result out through the process exit code (runMainAndExit in main.mjs) -- Environment.Exit is broken under wasm, the return value is not. This Main reverts to actual hosting when M4 gives it an engine to host.
internal static class Program
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "WasmHost.props publishes untrimmed (PublishTrimmed=false) and roots this assembly besides; no fixture can be trimmed away")]
    private static int Main()
    {
        (int failures, string report) = RunnerWasm.Run(typeof(Program).Assembly);
        Console.WriteLine(report);
        return failures > 0 ? 1 : 0;
    }
}
