using NUnit.Framework;

namespace Buckminster.Tests;

// Pins the NUnit engine features the wasm in-host runner must support beyond bare [TestFixture]/[Test] -- the exact capability gained by replacing RunnerMini with the real NUnit engine. Runs on all six matrix cells; if a wasm runner regression ever drops one of these features, this fixture is the loud proof.
[TestFixture]
public class NUnitFeatureTests
{
    private int setUpCounter;

    [SetUp]
    public void SetUp()
    {
        setUpCounter = 41;
    }

    [Test]
    public void SetUpRunsBeforeEachTest()
    {
        setUpCounter += 1;
        Assert.That(setUpCounter, Is.EqualTo(42));
    }

    [Test]
    public void SetUpStateDoesNotLeakBetweenTests()
    {
        // Identical mutation to the test above: whichever runs second only sees 42 if SetUp reran.
        setUpCounter += 1;
        Assert.That(setUpCounter, Is.EqualTo(42));
    }

    [TestCase(1, 2, 3)]
    [TestCase(-5, 5, 0)]
    [TestCase(int.MaxValue, 0, int.MaxValue)]
    public void TestCaseSuppliesArguments(int a, int b, int expected)
    {
        Assert.That(a + b, Is.EqualTo(expected));
    }

    [Test]
    public void ValuesExpandsParameters([Values(1, 2, 3)] int value)
    {
        Assert.That(value, Is.GreaterThan(0));
    }

    [Test]
    public void AssertMultipleAggregates()
    {
        Assert.Multiple(() =>
        {
            Assert.That(1 + 1, Is.EqualTo(2));
            Assert.That("buckminster", Does.StartWith("buck"));
        });
    }
}
