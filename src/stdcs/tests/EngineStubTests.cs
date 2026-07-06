using NUnit.Framework;

namespace Buckminster.Tests;

[TestFixture]
public class EngineStubTests
{
    [Test]
    public void DescribeReturnsNonEmpty()
    {
        Assert.That(EngineStub.Describe(), Is.Not.Null.And.Not.Empty);
    }
}
