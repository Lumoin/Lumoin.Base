using System.Diagnostics.Metrics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Tests;

/// <summary>
/// Tests for <see cref="Utf8StringInterner"/>: a concurrent, GC-evictable interner that deduplicates recurring
/// UTF-8 values, bounds its working set by hot/cold generation eviction, and keeps already-returned values valid
/// after eviction because each roots its own managed bytes.
/// </summary>
[TestClass]
public sealed class Utf8StringInternerTests
{
    [TestMethod]
    public void InternReturnsEqualValueForDuplicateBytes()
    {
        Utf8StringInterner interner = new();

        Utf8String first = interner.Intern("http://example.org/resource"u8);
        Utf8String second = interner.Intern("http://example.org/resource"u8);

        Assert.AreEqual(first, second);
        Assert.AreEqual(1, interner.Count);
    }


    [TestMethod]
    public void InternCopiesIntoOwnedMemoryNotCallerSpan()
    {
        Utf8StringInterner interner = new();
        byte[] source = "mutable"u8.ToArray();

        Utf8String interned = interner.Intern(source);
        Array.Clear(source);

        //The interned value must hold its own copy, not alias the caller's now-cleared buffer.
        Assert.AreEqual("mutable", interned.ToString());
    }


    [TestMethod]
    public void InternStringDeduplicatesWithByteVersion()
    {
        Utf8StringInterner interner = new();

        Utf8String fromBytes = interner.Intern("term"u8);
        Utf8String fromString = interner.Intern("term");

        Assert.AreEqual(fromBytes, fromString);
        Assert.AreEqual(1, interner.Count);
    }


    [TestMethod]
    public void InternLongStringFallsBackToPooledEncodeBuffer()
    {
        Utf8StringInterner interner = new();
        string longValue = new('a', 500);

        Utf8String interned = interner.Intern(longValue);

        Assert.AreEqual(longValue, interned.ToString());
        Assert.AreEqual(interned, interner.Intern(Encoding.UTF8.GetBytes(longValue)));
    }


    [TestMethod]
    public void InternEmptyValueRoundTrips()
    {
        Utf8StringInterner interner = new();

        Utf8String empty = interner.Intern("");

        Assert.IsTrue(empty.IsEmpty);
        Assert.AreEqual(empty, interner.Intern(ReadOnlySpan<byte>.Empty));
    }


    [TestMethod]
    public void InternStringRejectsNull()
    {
        Utf8StringInterner interner = new();

        Assert.ThrowsExactly<ArgumentNullException>(() => interner.Intern((string)null!));
    }


    [TestMethod]
    public void ConstructorRejectsNonPositiveMaxEntries()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Utf8StringInterner(maxEntries: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Utf8StringInterner(maxEntries: -1));
    }


    [TestMethod]
    public void ValidationIsOnByDefaultAndCanBeOptedOut()
    {
        Utf8StringInterner validating = new();
        Assert.ThrowsExactly<ArgumentException>(() => validating.Intern([0xC3, 0x28]));

        Utf8StringInterner lenient = new(validateOnIntern: false);
        Utf8String interned = lenient.Intern([0xC3, 0x28]);
        Assert.AreEqual(2, interned.Length);
    }


    [TestMethod]
    public void TryGetFindsInternedAndMissesAbsent()
    {
        Utf8StringInterner interner = new();

        Assert.IsFalse(interner.TryGet("absent"u8, out _));

        Utf8String interned = interner.Intern("present"u8);
        Assert.IsTrue(interner.TryGet("present"u8, out Utf8String found));
        Assert.AreEqual(interned, found);
    }


    [TestMethod]
    public void ClearEmptiesTheInterner()
    {
        Utf8StringInterner interner = new();
        interner.Intern("a"u8);
        interner.Intern("b"u8);

        interner.Clear();

        Assert.AreEqual(0, interner.Count);
        Assert.IsFalse(interner.TryGet("a"u8, out _));
    }


    [TestMethod]
    public void EvictionBoundsTheLiveSetToAboutTwiceMaxEntries()
    {
        const int maxEntries = 8;
        Utf8StringInterner interner = new(maxEntries: maxEntries);

        //A long stream of unique values: without eviction Count would reach the stream length.
        for(int i = 0; i < maxEntries * 20; i++)
        {
            interner.Intern(Encoding.UTF8.GetBytes($"unique-value-{i}"));
        }

        Assert.IsLessThanOrEqualTo(maxEntries * 2, interner.Count,
            "Two-generation eviction must bound a unique stream to about twice MaxEntries.");
    }


    [TestMethod]
    public void EvictedValueStillHeldStaysReadable()
    {
        const int maxEntries = 4;
        Utf8StringInterner interner = new(maxEntries: maxEntries);

        Utf8String held = interner.Intern("survivor"u8);

        //Push enough unique values to rotate several times, evicting the original from both generations.
        for(int i = 0; i < maxEntries * 8; i++)
        {
            interner.Intern(Encoding.UTF8.GetBytes($"filler-{i}"));
        }

        Assert.IsFalse(interner.TryGet("survivor"u8, out _), "The original value should have been evicted.");
        //The GC-safety invariant: a handed-out value roots its own bytes, so eviction never invalidates it.
        Assert.AreEqual("survivor", held.ToString());
    }


    [TestMethod]
    public void ColdHitIsPromotedAndSurvivesNextRotation()
    {
        const int maxEntries = 4;
        Utf8StringInterner interner = new(maxEntries: maxEntries);

        Utf8String original = interner.Intern("keeper"u8);

        //Fill hot to force a rotation, moving "keeper" into the cold generation.
        for(int i = 0; i < maxEntries; i++)
        {
            interner.Intern(Encoding.UTF8.GetBytes($"a-{i}"));
        }

        //Touch "keeper" so the cold hit promotes it back into hot.
        Assert.AreEqual(original, interner.Intern("keeper"u8));

        //Another rotation drops the old cold; the promoted copy must keep "keeper" alive.
        for(int i = 0; i < maxEntries; i++)
        {
            interner.Intern(Encoding.UTF8.GetBytes($"b-{i}"));
        }

        Assert.IsTrue(interner.TryGet("keeper"u8, out _), "A promoted cold hit must survive the next rotation.");
    }


    [TestMethod]
    public void CustomComparerGivesDeterministicHashAndStillInterns()
    {
        Utf8StringComparer deterministic = Utf8StringComparer.Create(Fnv1a32);
        Utf8StringInterner interner = new(comparer: deterministic);

        Utf8String interned = interner.Intern("stable"u8);

        Assert.AreEqual(Utf8String.NormalizeHash(Fnv1a32("stable"u8)), interned.GetHashCode());
        Assert.AreEqual(interned, interner.Intern("stable"u8));
    }


    [TestMethod]
    public void ConcurrentInterningOfOneValueProducesASingleEntry()
    {
        Utf8StringInterner interner = new();
        byte[] value = "contended"u8.ToArray();

        Parallel.For(0, 128, _ => interner.Intern(value));

        //GetOrAdd settles the race, so identical bytes collapse to exactly one interned entry.
        Assert.AreEqual(1, interner.Count);
    }


    [TestMethod]
    public void ConcurrentInterningStaysConsistentAndBounded()
    {
        const int maxEntries = 64;
        Utf8StringInterner interner = new(maxEntries: maxEntries);

        Parallel.For(0, 20_000, i =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes($"value-{i}");
            Utf8String interned = interner.Intern(bytes);
            Assert.IsTrue(interned.SequenceEqual(bytes), "Every interned value must round-trip its bytes.");
        });

        //20,000 unique values stream through under contention; eviction keeps the live set near twice MaxEntries.
        Assert.IsLessThanOrEqualTo(maxEntries * 4, interner.Count);
    }


    [TestMethod]
    public void OversizedValuesAreReturnedButNotCached()
    {
        Utf8StringInterner interner = new(maxValueLength: 8);
        byte[] big = Encoding.UTF8.GetBytes(new string('x', 64));

        Utf8String oversized = interner.Intern(big);

        //Returned correctly, but never retained: the guard keeps one huge value from bloating the resident set.
        Assert.AreEqual(64, oversized.Length);
        Assert.IsFalse(interner.TryGet(big, out _));
        Assert.AreEqual(0, interner.Count);

        //A value within the limit is cached and deduplicated as usual.
        Utf8String small = interner.Intern("small"u8);
        Assert.IsTrue(interner.TryGet("small"u8, out _));
        Assert.AreEqual(small, interner.Intern("small"u8));
        Assert.AreEqual(1, interner.Count);
    }


    [TestMethod]
    public void ConstructorRejectsNonPositiveMaxValueLength()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Utf8StringInterner(maxValueLength: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Utf8StringInterner(maxValueLength: -1));
    }


    [TestMethod]
    public void MeterRecordsInternOperationsHitsAndRotations()
    {
        using Meter meter = new("Test.Utf8StringInterner.Metrics");
        long operations = 0;
        long hits = 0;
        long rotations = 0;

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if(instrument.Meter == meter)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if(instrument.Name == Utf8StringInternerMetrics.InternOperationsTotal)
            {
                operations += measurement;
            }
            else if(instrument.Name == Utf8StringInternerMetrics.InternHitsTotal)
            {
                hits += measurement;
            }
            else if(instrument.Name == Utf8StringInternerMetrics.RotationsTotal)
            {
                rotations += measurement;
            }
        });
        listener.Start();

        Utf8StringInterner interner = new(maxEntries: 4, meter: meter);
        interner.Intern("repeat"u8);
        interner.Intern("repeat"u8);
        for(int i = 0; i < 12; i++)
        {
            interner.Intern(Encoding.UTF8.GetBytes($"v-{i}"));
        }

        Assert.IsGreaterThanOrEqualTo(14, operations, "Every intern call records an operation.");
        Assert.IsGreaterThanOrEqualTo(1, hits, "The repeated value records a hit.");
        Assert.IsGreaterThanOrEqualTo(1, rotations, "Filling past MaxEntries records rotations.");
    }


    [TestMethod]
    public void AmbientInstanceIsSettable()
    {
        Utf8StringInterner? previous = Utf8StringInterner.Instance;
        try
        {
            Utf8StringInterner interner = new();
            Utf8StringInterner.Instance = interner;

            Assert.AreSame(interner, Utf8StringInterner.Instance);
        }
        finally
        {
            Utf8StringInterner.Instance = previous;
        }
    }


    [TestMethod]
    public void ConcurrentClearAndInternStayConsistent()
    {
        Utf8StringInterner interner = new(maxEntries: 16);

        //Interleave a storm of Clears with interning across threads: every returned value must still round-trip,
        //and a Clear must never be silently undone by a concurrent rotation.
        Parallel.For(0, 20_000, i =>
        {
            if(i % 250 == 0)
            {
                interner.Clear();
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes($"value-{i}");
            Utf8String interned = interner.Intern(bytes);
            Assert.IsTrue(interned.SequenceEqual(bytes), "A value interned during a Clear must never be torn.");
        });

        //A final Clear with no concurrent interning must leave the interner empty.
        interner.Clear();
        Assert.AreEqual(0, interner.Count);
    }


    /// <summary>
    /// A small, fixed-seed, dependency-free deterministic hash standing in for a cross-process-stable function.
    /// </summary>
    private static int Fnv1a32(ReadOnlySpan<byte> bytes)
    {
        const uint OffsetBasis = 2166136261;
        const uint Prime = 16777619;

        uint hash = OffsetBasis;
        foreach(byte b in bytes)
        {
            hash = (hash ^ b) * Prime;
        }

        return unchecked((int)hash);
    }
}
