using System;
using Buckminster.Tests;

namespace Buckminster.Host.WasmNode;

// The Node-hosted wasm host (the wasm-desktop run target). Its job is running the C# test suite in-host and carrying the result out through the process exit code (runMainAndExit in main.mjs) -- Environment.Exit is broken under wasm, the return value is not. This is the matrix's cs-wasm-node cell, a permanent role, not scaffolding (the M3-era idea that M4 would "revert" this to hosting was wrong); real wasm-desktop hosting gets designed when a milestone needs it, alongside the runner role rather than replacing it.
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
