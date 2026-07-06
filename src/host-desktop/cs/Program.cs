using System;

namespace Buckminster.Host.Desktop;

// The desktop host. The real ~20-line PumpEvents/Tick/Render loop arrives with M1/M4; until then this just proves the project wiring.
internal static class Program
{
    private static void Main()
    {
        Console.WriteLine(EngineStub.Describe());
    }
}
