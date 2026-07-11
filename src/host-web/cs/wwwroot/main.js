// Browser entry point: boot the runtime, run the in-host test suite, render the report into the page. The final report line is the driver's sentinel (BUCK-TEST-EXIT:<code>); a page failure must land on the PAGE too, never just the console -- and the catch path appends its own failing sentinel so the driver sees a loud failure, not a timeout.
import { dotnet } from './_framework/dotnet.js'

// buckTestOut, not "out": defensive habit from the Rust harness, where emscripten's global `var out` clobbered the page's variable.
const buckTestOut = document.getElementById('out');
try {
    const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
    const exports = await getAssemblyExports(getConfig().mainAssemblyName);
    buckTestOut.textContent = exports.Buckminster.Host.Web.ExportsTest.RunTests();
    await runMain();
} catch (error) {
    buckTestOut.textContent = 'in-host test run FAILED:\n' + error + '\nBUCK-TEST-EXIT:1';
    buckTestOut.style.color = 'red';
    throw error;
}
