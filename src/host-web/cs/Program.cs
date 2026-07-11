using System;
using System.Runtime.InteropServices.JavaScript;
using Buckminster.Tests;

namespace Buckminster.Host.Web;

// The browser host. Its M3 job is running the C# test suite in-host and handing the report to main.js for rendering; this reverts to actual hosting when M4 gives it an engine to host.
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("Buckminster.Host.Web runtime up");
        return 0;
    }
}

// JS-facing surface (main.js calls this via getAssemblyExports). Host-executable surface, not Buckminster library API. (Category-first naming: Exports is the kind of thing, Test the instance.)
public partial class ExportsTest
{
    [JSExport]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "WasmHost.props publishes untrimmed (PublishTrimmed=false) and roots this assembly besides; no fixture can be trimmed away")]
    internal static string RunTests()
    {
        // The final line is the driver's sentinel (tools/lib/wasmbrowser.py): the report ends with BUCK-TEST-EXIT:<0|1>.
        (int failures, string report) = RunnerMini.Run(typeof(ExportsTest).Assembly);
        return report + "\nBUCK-TEST-EXIT:" + (failures > 0 ? "1" : "0");
    }
}
