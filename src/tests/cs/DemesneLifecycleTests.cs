using Buckminster;
using Buckminster.Ffi;
using NUnit.Framework;

namespace Buckminster.Tests;

// The demesne lifecycle (M5.75 ABI split): the raw FFI registry contracts inherited from the retired per-instance engine -- create/destroy, handle staleness, per-instance poison-on-panic -- plus the near-empty C# Demesne class and its deliberately-dormant Current. The demesne is render residency; it grows real content at M6, and these tests are the registry armor waiting for it.
[TestFixture]
public class DemesneLifecycleTests
{
    [TearDown]
    public void TearDown()
    {
        // One leaked test-set value would poison later assertions on the shared NUnit thread.
        Demesne.Current = null;
    }

    [Test]
    public void CreateDestroyRoundtrip()
    {
        FfiCode code = NativeMethods.buck_demesne_create(out ulong demesne);
        Assert.That(code, Is.EqualTo(FfiCode.Ok));
        Assert.That(demesne, Is.Not.Zero);
        Assert.That(NativeMethods.buck_demesne_destroy(demesne), Is.EqualTo(FfiCode.Ok));
    }

    [Test]
    public void DestroyTwiceIsInvalidArgument()
    {
        NativeMethods.buck_demesne_create(out ulong demesne);
        Assert.That(NativeMethods.buck_demesne_destroy(demesne), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_demesne_destroy(demesne), Is.EqualTo(FfiCode.InvalidArgument));
        Assert.That(NativeMethods.LastErrorMessage(), Is.Not.Null);
    }

    [Test]
    public void NullHandleIsInvalidArgument()
    {
        Assert.That(NativeMethods.buck_demesne_destroy(0), Is.EqualTo(FfiCode.InvalidArgument));
        Assert.That(NativeMethods.buck_demesne_test_panic(0), Is.EqualTo(FfiCode.InvalidArgument));
    }

    [Test]
    public void StaleHandleAfterSlotReuseIsInvalidArgument()
    {
        NativeMethods.buck_demesne_create(out ulong first);
        NativeMethods.buck_demesne_destroy(first);
        NativeMethods.buck_demesne_create(out ulong second);
        // The slot got reused (LIFO free list) but the generation moved on -- the old handle must not alias the new demesne (a live second would return Panic from the probe, never InvalidArgument).
        Assert.That(NativeMethods.buck_demesne_test_panic(first), Is.EqualTo(FfiCode.InvalidArgument));
        Assert.That(NativeMethods.buck_demesne_test_panic(second), Is.EqualTo(FfiCode.Panic));
        NativeMethods.buck_demesne_destroy(second);
    }

    [Test]
    public void PanicPoisonsTheDemesneButDestroyStillWorks()
    {
        NativeMethods.buck_demesne_create(out ulong demesne);
        Assert.That(NativeMethods.buck_demesne_test_panic(demesne), Is.EqualTo(FfiCode.Panic));
        // The demesne-scoped inner catch is a different path from guard's outer one -- pin that the original panic text survives it.
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("deliberate panic"));
        Assert.That(NativeMethods.buck_demesne_test_panic(demesne), Is.EqualTo(FfiCode.Poisoned));
        Assert.That(NativeMethods.LastErrorMessage(), Does.Contain("poisoned"));
        Assert.That(NativeMethods.buck_demesne_destroy(demesne), Is.EqualTo(FfiCode.Ok));
    }

    [Test]
    public void PoisonIsPerDemesneNotProcess()
    {
        NativeMethods.buck_demesne_create(out ulong poisonedDemesne);
        NativeMethods.buck_demesne_create(out ulong healthyDemesne);
        NativeMethods.buck_demesne_test_panic(poisonedDemesne);
        // The sibling demesne and fresh creation must be untouched -- the panic poisons one demesne, not the registry.
        Assert.That(NativeMethods.buck_demesne_test_panic(healthyDemesne), Is.EqualTo(FfiCode.Panic));
        Assert.That(NativeMethods.buck_demesne_create(out ulong thirdDemesne), Is.EqualTo(FfiCode.Ok));
        Assert.That(NativeMethods.buck_demesne_destroy(poisonedDemesne), Is.EqualTo(FfiCode.Ok));
        NativeMethods.buck_demesne_destroy(healthyDemesne);
        NativeMethods.buck_demesne_destroy(thirdDemesne);
    }

    [Test]
    public void DemesneClassDisposesIdempotentlyAndCurrentIsSettable()
    {
        // The C# face: create-through-ctor, idempotent Dispose, and the dormant thread-local Current slot (nothing engine-side reads it until M6; this pins the pattern).
        Demesne demesne = new Demesne();
        Assert.That(Demesne.Current, Is.Null);
        Demesne.Current = demesne;
        Assert.That(Demesne.Current, Is.SameAs(demesne));
        Demesne.Current = null;
        demesne.Dispose();
        demesne.Dispose();
    }
}
