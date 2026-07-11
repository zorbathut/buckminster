using System;
using Buckminster.Ffi;

namespace Buckminster.Host.WasmNode;

// The Node-hosted wasm host (the wasm-desktop run target). Until M3's in-host test runner arrives, its whole job is printing the FFI smoke lines to the terminal -- proof the Rust staticlib linked and runs.
internal static class Program
{
    private static int Main()
    {
        foreach (string line in FfiSmoke.Run())
        {
            Console.WriteLine(line);
        }
        return 0;
    }
}
