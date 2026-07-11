// Node entry point (template-shaped, .NET 10 wasmconsole): boot the runtime, run C# Main, exit with its code.
import { dotnet } from './_framework/dotnet.js'

const { runMainAndExit } = await dotnet.create();

await runMainAndExit();
