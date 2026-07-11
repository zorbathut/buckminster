using System;
using System.Runtime.InteropServices.JavaScript;
using Buckminster.Ffi;

namespace Buckminster.Host.Web;

// The browser host. Until M3's in-host test runner arrives, its whole job is handing the FFI smoke lines to main.js for rendering -- proof the Rust staticlib linked and runs in the browser.
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("Buckminster.Host.Web runtime up");
        return 0;
    }
}

// JS-facing surface (main.js calls this via getAssemblyExports). This is host-executable surface, not Buckminster library API -- the raw-FFI-stays-internal rule is about the library. (Category-first naming: Exports is the kind of thing, Smoke the instance.)
public partial class ExportsSmoke
{
    [JSExport]
    internal static string RunSmoke()
    {
        return string.Join("\n", FfiSmoke.Run());
    }
}
