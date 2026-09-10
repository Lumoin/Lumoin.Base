using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Tests;

/// <summary>
/// Tests for <see cref="EmptyMemoryOwner"/>: a shared, zero-length, no-op-dispose owner that stays usable
/// no matter how often it is disposed.
/// </summary>
[TestClass]
public sealed class EmptyMemoryOwnerTests
{
    [TestMethod]
    public void InstanceIsAZeroLengthSingletonWithNoOpDispose()
    {
        //Two separate property reads must observe the same shared instance; the locals keep the
        //identity check from reading as a constant-true assertion (MSTEST0032).
        var firstRead = EmptyMemoryOwner.Instance;
        var secondRead = EmptyMemoryOwner.Instance;
        Assert.AreSame(firstRead, secondRead);
        Assert.IsEmpty(EmptyMemoryOwner.Instance.Memory);

        //No-op dispose: safe to call repeatedly, and the instance stays usable afterwards.
        EmptyMemoryOwner.Instance.Dispose();
        EmptyMemoryOwner.Instance.Dispose();
        Assert.IsEmpty(EmptyMemoryOwner.Instance.Memory);
    }
}
