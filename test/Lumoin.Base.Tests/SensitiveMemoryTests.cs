using System.Buffers;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Tests;

/// <summary>
/// Tests for the <see cref="SensitiveMemory"/> base: it owns its buffer, exposes only read-only views, clears the
/// bytes on disposal, and compares by content.
/// </summary>
[TestClass]
public sealed class SensitiveMemoryTests
{
    /// <summary>A concrete subclass for exercising the abstract base.</summary>
    private sealed class TestDocument(IMemoryOwner<byte> owner, Tag tag, Activity? lifetime = null): SensitiveMemory(owner, tag, lifetime);

    /// <summary>
    /// An owner over a caller-held array, whose Dispose does NOT clear, so a test can observe that
    /// SensitiveMemory.Dispose itself wiped the bytes.
    /// </summary>
    private sealed class HeldOwner(byte[] buffer): IMemoryOwner<byte>
    {
        public byte[] Buffer => buffer;

        public Memory<byte> Memory => buffer;

        public void Dispose()
        {
        }
    }


    [TestMethod]
    public void ExposesTheBytesAsReadOnly()
    {
        byte[] bytes = [10, 20, 30];
        using TestDocument document = new(new HeldOwner(bytes), Tag.Empty);

        CollectionAssert.AreEqual(bytes, document.AsReadOnlySpan().ToArray());
        CollectionAssert.AreEqual(bytes, document.AsReadOnlyMemory().ToArray());
    }


    [TestMethod]
    public void DisposeClearsTheUnderlyingBytes()
    {
        byte[] bytes = [1, 2, 3, 4];
        HeldOwner owner = new(bytes);
        TestDocument document = new(owner, Tag.Empty);

        document.Dispose();

        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, owner.Buffer, "Dispose must wipe the sensitive bytes.");
    }


    [TestMethod]
    public void AccessingAfterDisposeThrows()
    {
        TestDocument document = new(new HeldOwner([1, 2, 3]), Tag.Empty);
        document.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => document.AsReadOnlySpan().Length);
    }


    [TestMethod]
    public void EqualityIsByContent()
    {
        using TestDocument a = new(new HeldOwner([1, 2, 3]), Tag.Empty);
        using TestDocument b = new(new HeldOwner([1, 2, 3]), Tag.Empty);
        using TestDocument c = new(new HeldOwner([1, 2, 4]), Tag.Empty);

        Assert.AreEqual(a, b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        Assert.AreNotEqual(a, c);
    }


    [TestMethod]
    public void ConstructorRejectsNullOwner()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new TestDocument(null!, Tag.Empty));
    }


    [TestMethod]
    public void LifetimeActivityIsStoppedAndStampedOnDispose()
    {
        using ActivitySource source = new("Test.SensitiveMemory.Lifetime");
        using ActivityListener listener = new()
        {
            ShouldListenTo = static s => s.Name == "Test.SensitiveMemory.Lifetime",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        Activity activity = source.StartActivity("lifetime")!;
        Assert.IsNotNull(activity);

        TestDocument document = new(new HeldOwner([1, 2, 3]), Tag.Empty, activity);
        document.Dispose();

        Assert.IsTrue(activity.IsStopped, "Dispose must stop the bounded lifetime activity.");
        Assert.IsNotNull(activity.GetTagItem(SensitiveMemoryTelemetry.LifetimeMs));
    }


    [TestMethod]
    public void DisposingEmptyOwnerBackedValueDoesNotPoisonTheSingleton()
    {
        TestDocument shared = new(EmptyMemoryOwner.Instance, Tag.Empty);

        shared.Dispose();

        //The guard skipped the disposed transition, so a shared empty stays usable for later callers.
        Assert.AreEqual(0, shared.AsReadOnlySpan().Length);
        Assert.AreEqual(0, shared.AsReadOnlyMemory().Length);
    }
}
