using CsCheck;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Tests;

/// <summary>
/// Property-based tests using CsCheck for <see cref="Utf8StringInterner"/>: the strict
/// <see cref="Utf8StringInterner.TryIntern(string, out Utf8String)"/> path agrees with the lossy
/// <see cref="Utf8StringInterner.Intern(string)"/> on well-formed UTF-16, rejects a lone surrogate without
/// interning it, and interning stays idempotent while the live set stays inside the documented bound.
/// </summary>
[TestClass]
public sealed class Utf8StringInternerCsCheckTests
{
    /// <summary>
    /// Text fragments that are always well-formed UTF-16: ASCII, BMP scalars outside the surrogate range, and
    /// whole surrogate pairs. Written as code points so the source stays 7-bit ASCII, and chosen to span one-,
    /// two-, three- and four-byte UTF-8 encodings.
    /// </summary>
    private static readonly string[] WellFormedFragments =
    [
        "a",
        "b",
        "Z",
        "0",
        "9",
        " ",
        "-",
        "/",
        ":",
        char.ConvertFromUtf32(0x00E4),
        char.ConvertFromUtf32(0x03BB),
        char.ConvertFromUtf32(0x4E2D),
        char.ConvertFromUtf32(0x20AC),
        char.ConvertFromUtf32(0x1F600),
        char.ConvertFromUtf32(0x1F30D)
    ];


    /// <summary>
    /// Generates well-formed UTF-16 strings by concatenating whole scalars, so a lone surrogate can never appear
    /// by accident. Ill-formed input is generated deliberately instead, in
    /// <see cref="PropertyTryInternRejectsALoneSurrogateWithoutInterningIt"/>.
    /// </summary>
    private static Gen<string> WellFormedText =>
        Gen.OneOfConst(WellFormedFragments).Array[0, 24].Select(parts => string.Concat(parts));


    [TestMethod]
    public void PropertyTryInternAcceptsWellFormedTextAndAgreesWithIntern()
    {
        WellFormedText.Sample(value =>
        {
            Utf8StringInterner interner = new();

            Assert.IsTrue(interner.TryIntern(value, out Utf8String strict),
                $"Well-formed UTF-16 of length {value.Length} must intern through the strict path.");
            Assert.AreEqual(value, strict.ToString(),
                "The strict path must round-trip well-formed text unchanged.");

            //Both paths delegate to one intern, so the strict and lossy results are the same interned value.
            Assert.AreEqual(strict, interner.Intern(value),
                "The strict and lossy paths must share one table for well-formed text.");
            Assert.IsTrue(interner.TryGet(System.Text.Encoding.UTF8.GetBytes(value), out _),
                "An accepted value must be visible to a byte probe.");
        });
    }


    [TestMethod]
    public void PropertyRepeatedInterningIsIdempotentAndCountStaysBounded()
    {
        Gen.Select(Gen.Int[1, 16], WellFormedText.Array[1, 64]).Sample(generated =>
        {
            (int maxEntries, string[] values) = generated;

            Utf8StringInterner interner = new(maxEntries: maxEntries);

            foreach(string value in values)
            {
                Utf8String first = interner.Intern(value);
                Utf8String second = interner.Intern(value);

                //Utf8String equality is pure content comparison, so two independently allocated arrays of equal
                //bytes compare equal; only equal backing memory proves the second intern was served from cache.
                Assert.AreEqual(first.Memory, second.Memory,
                    $"Interning text of length {value.Length} twice must return the one interned backing array.");

                //Two-generation eviction bounds the live set: hot rotates the moment it reaches MaxEntries, and
                //cold holds at most one full generation, so the resident count never passes twice MaxEntries.
                Assert.IsLessThanOrEqualTo(maxEntries * 2, interner.Count,
                    $"With MaxEntries {maxEntries} the live set must stay inside twice that bound.");

                //The upper bound alone is satisfied by an interner that caches nothing: a rotation moves a full hot
                //generation to cold rather than dropping it, so at least one value stays resident after an intern.
                Assert.IsGreaterThanOrEqualTo(1, interner.Count,
                    $"With MaxEntries {maxEntries} an interned value must leave the live set non-empty.");
            }
        });
    }


    [TestMethod]
    public void PropertyTryInternRejectsALoneSurrogateWithoutInterningIt()
    {
        //The fragments are whole scalars, so a surrogate spliced between them can never pair with a neighbour:
        //every generated string here is genuinely ill-formed UTF-16.
        Gen.Select(WellFormedText, Gen.Int[0xD800, 0xDFFF], WellFormedText).Sample(generated =>
        {
            (string prefix, int surrogate, string suffix) = generated;

            Utf8StringInterner interner = new();
            string illFormed = prefix + (char)surrogate + suffix;

            Assert.IsFalse(interner.TryIntern(illFormed, out Utf8String result),
                $"A lone surrogate U+{surrogate:X4} must be reported rather than replaced.");
            Assert.AreEqual(Utf8String.Empty, result,
                "A rejected string must yield the empty value.");
            Assert.AreEqual(0, interner.Count,
                "A rejected string must leave nothing interned.");
        });
    }
}
