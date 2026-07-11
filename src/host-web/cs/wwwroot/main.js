// Browser entry point (template-shaped, .NET 10 wasmbrowser): boot the runtime, run the FFI smoke test, render the result into the page. A failure must land on the PAGE, not just the console -- this page's whole job is being looked at.
import { dotnet } from './_framework/dotnet.js'

const out = document.getElementById('out');
try {
    const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
    const exports = await getAssemblyExports(getConfig().mainAssemblyName);
    out.innerText = exports.Buckminster.Host.Web.ExportsSmoke.RunSmoke();
    await runMain();
} catch (error) {
    out.innerText = 'FFI smoke test FAILED:\n' + error;
    out.style.color = 'red';
    throw error;
}
