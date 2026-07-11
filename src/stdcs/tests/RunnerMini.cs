using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Buckminster.Tests;

// The in-host test runner for the wasm targets, where the real NUnit runners can't go (NUnitLite's engine needs threads, Console redirection, and Environment.Exit -- all broken or hostile under browser-wasm). It runs [TestFixture]/[Test] methods via reflection and NUnit's standalone assertion library. The support guarantee is attribute-and-exception-scoped, enforced by Audit(): any NUnit attribute outside the allowlist, and any NUnit result-state the runner doesn't implement, is a hard error naming the feature -- extending this runner (or moving the hosts to a real one) is the fix, silence never is. Reverts to nothing when a real cross-target runner exists.
internal static class RunnerMini
{
    // Trimming a fixture away would silently shrink the suite -- the exact bug class this runner exists to prevent -- so callers must root the test assembly (WasmHost.props sets TrimmerRootAssembly) and acknowledge with a suppression.
    [RequiresUnreferencedCode("reflects over the whole test assembly; the caller must root it against trimming (TrimmerRootAssembly)")]
    internal static (int Failures, string Report) Run(Assembly assembly)
    {
        Audit(assembly);
        StringBuilder report = new StringBuilder();
        int passed = 0;
        int failed = 0;
        foreach (Type type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<TestFixtureAttribute>() == null)
            {
                continue;
            }
            object fixture = Activator.CreateInstance(type) ?? throw new InvalidOperationException($"could not instantiate fixture {type.Name}");
            try
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (method.GetCustomAttribute<TestAttribute>() == null)
                    {
                        continue;
                    }
                    string name = $"{type.Name}.{method.Name}";
                    string? failure = RunOne(fixture, method);
                    if (failure == null)
                    {
                        passed += 1;
                        report.AppendLine($"PASS {name}");
                    }
                    else
                    {
                        failed += 1;
                        report.AppendLine($"FAIL {name}");
                        report.AppendLine(failure);
                    }
                }
            }
            finally
            {
                // NUnit's contract disposes IDisposable fixtures; diverging silently from dotnet test semantics is the exact bug class this runner promises not to have.
                if (fixture is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        if (passed + failed == 0)
        {
            // Zero tests means the suite didn't reach this assembly (broken source links, glob drift) -- reporting success would be the silent shrink this runner exists to prevent.
            throw new InvalidOperationException("RunnerMini found no tests in " + assembly.GetName().Name + " -- the test sources didn't make it into this assembly.");
        }
        report.AppendLine($"total: {passed + failed}, passed: {passed}, failed: {failed}");
        return (failed, report.ToString());
    }

    private static string? RunOne(object fixture, MethodInfo method)
    {
        try
        {
            method.Invoke(method.IsStatic ? null : fixture, null);
            return null;
        }
        catch (TargetInvocationException wrapper) when (wrapper.InnerException != null)
        {
            Exception inner = wrapper.InnerException;
            if (inner is SuccessException)
            {
                return null;
            }
            if (inner is AssertionException || inner is MultipleAssertException)
            {
                // Both carry genuine assertion failures (MultipleAssertException is Assert.Multiple's aggregate).
                return inner.ToString();
            }
            if (inner is ResultStateException)
            {
                // Not a test failure -- a test asked for a result state this runner doesn't implement (Ignore, Inconclusive, ...). That must break the build, not skew the counts.
                throw new NotSupportedException($"RunnerMini doesn't implement the NUnit result state {inner.GetType().Name} (from {method.Name}) -- extend the runner or move the wasm hosts to a full one.", inner);
            }
            return inner.ToString();
        }
    }

    // The allowlist scan: everything this runner executes is exactly {TestFixture, Test}; any other NUnit attribute anywhere in the assembly (assembly-level parallelism settings, [SetUp], [TestCase], [SetUpFixture], ...) means a test relies on machinery this runner doesn't have. Known scope limits, deliberate: parameter-level attributes ([Values], [Range]) aren't scanned (they fail loudly but cryptically as TargetParameterCountException), and non-attribute NUnit API in test bodies is covered only via the ResultStateException mapping in RunOne.
    [RequiresUnreferencedCode("reflects over the whole test assembly")]
    private static void Audit(Assembly assembly)
    {
        List<string> problems = new List<string>();
        foreach (CustomAttributeData attribute in assembly.GetCustomAttributesData())
        {
            if (IsForeignNUnitAttribute(attribute.AttributeType, typeof(TestFixtureAttribute), typeof(TestAttribute)))
            {
                problems.Add($"assembly-level [{attribute.AttributeType.Name}]");
            }
        }
        foreach (Type type in assembly.GetTypes())
        {
            foreach (CustomAttributeData attribute in type.GetCustomAttributesData())
            {
                if (IsForeignNUnitAttribute(attribute.AttributeType, typeof(TestFixtureAttribute)))
                {
                    problems.Add($"{type.Name}: [{attribute.AttributeType.Name}]");
                }
            }
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (CustomAttributeData attribute in method.GetCustomAttributesData())
                {
                    if (IsForeignNUnitAttribute(attribute.AttributeType, typeof(TestAttribute)))
                    {
                        problems.Add($"{type.Name}.{method.Name}: [{attribute.AttributeType.Name}]");
                    }
                }
                if (method.GetCustomAttribute<TestAttribute>() != null && (typeof(Task).IsAssignableFrom(method.ReturnType) || method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() != null))
                {
                    // AsyncStateMachineAttribute also catches async void, which Invoke would silently "pass" at the first await -- worse than not running.
                    problems.Add($"{type.Name}.{method.Name}: async test");
                }
            }
        }
        if (problems.Count > 0)
        {
            throw new NotSupportedException("RunnerMini doesn't implement: " + string.Join("; ", problems) + " -- extend the runner or move the wasm hosts to a full one.");
        }
    }

    private static bool IsForeignNUnitAttribute(Type attributeType, params Type[] allowed)
    {
        if (attributeType.Namespace == null || !attributeType.Namespace.StartsWith("NUnit."))
        {
            return false;
        }
        return Array.IndexOf(allowed, attributeType) < 0;
    }
}
