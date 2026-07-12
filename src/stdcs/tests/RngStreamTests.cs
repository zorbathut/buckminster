using NUnit.Framework;

namespace Buckminster.Tests;

// PCG32-setseq against the reference implementation's own demo outputs: seed 42, stream 54 is the sequence O'Neill's pcg32-demo prints, so these constants are EXTERNAL truth, not self-blessed. Matching them on every target is the cross-target determinism check for the PRNG.
[TestFixture]
public class RngStreamTests
{
    [Test]
    public void MatchesThePcg32ReferenceSequence()
    {
        RngStream rng = new RngStream(42, 54);
        // First five outputs of the canonical pcg32_srandom(42, 54) demo.
        Assert.That(rng.NextUInt32(), Is.EqualTo(0xa15c02b7));
        Assert.That(rng.NextUInt32(), Is.EqualTo(0x7b47f409));
        Assert.That(rng.NextUInt32(), Is.EqualTo(0xba1d3330));
        Assert.That(rng.NextUInt32(), Is.EqualTo(0x83d2f293));
        Assert.That(rng.NextUInt32(), Is.EqualTo(0xbfa4784b));
    }

    [Test]
    public void NextUInt64IsHighWordFirst()
    {
        RngStream a = new RngStream(42, 54);
        RngStream b = new RngStream(42, 54);
        ulong combined = a.NextUInt64();
        uint high = b.NextUInt32();
        uint low = b.NextUInt32();
        // Draw order is part of the determinism contract; changing it is a determinism event, not a refactor.
        Assert.That(combined, Is.EqualTo(((ulong)high << 32) | low));
    }

    [Test]
    public void NextDoubleIsInUnitIntervalAndDeterministic()
    {
        RngStream a = new RngStream(1234, 1);
        RngStream b = new RngStream(1234, 1);
        for (int i = 0; i < 100; i++)
        {
            double value = a.NextDouble();
            Assert.That(value, Is.GreaterThanOrEqualTo(0.0));
            Assert.That(value, Is.LessThan(1.0));
            Assert.That(b.NextDouble(), Is.EqualTo(value));
        }
    }

    [Test]
    public void DifferentStreamIdsDiverge()
    {
        RngStream a = new RngStream(42, 1);
        RngStream b = new RngStream(42, 2);
        // Same seed, different stream: the setseq increment must actually separate them.
        Assert.That(a.NextUInt32(), Is.Not.EqualTo(b.NextUInt32()));
    }

    [Test]
    public void StateIsReadableAndDrawsMutateIt()
    {
        RngStream a = new RngStream(7, 9);
        RngStream b = new RngStream(7, 9);
        Assert.That(a.State, Is.EqualTo(b.State));
        Assert.That(a.StreamIncrement, Is.EqualTo(b.StreamIncrement));
        a.NextUInt32();
        // Drawing mutates the readable state -- so an owner hashing the stream catches PRNG misuse for free (PLAN.md: stream state is sim state).
        Assert.That(a.State, Is.Not.EqualTo(b.State));
        Assert.That(a.StreamIncrement, Is.EqualTo(b.StreamIncrement));
    }
}
