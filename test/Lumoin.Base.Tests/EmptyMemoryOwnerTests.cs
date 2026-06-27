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
        Assert.AreSame(EmptyMemoryOwner.Instance, EmptyMemoryOwner.Instance);
        Assert.AreEqual(0, EmptyMemoryOwner.Instance.Memory.Length);

        //No-op dispose: safe to call repeatedly, and the instance stays usable afterwards.
        EmptyMemoryOwner.Instance.Dispose();
        EmptyMemoryOwner.Instance.Dispose();
        Assert.AreEqual(0, EmptyMemoryOwner.Instance.Memory.Length);
    }
}
