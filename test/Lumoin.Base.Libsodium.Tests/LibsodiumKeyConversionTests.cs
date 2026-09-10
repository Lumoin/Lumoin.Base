using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Libsodium.Tests;

/// <summary>
/// Behavioral tests for <see cref="LibsodiumKeyConversion"/>: argument validation that holds
/// regardless of whether libsodium is present, and — gated behind
/// <see cref="LibsodiumTestEnvironment.RequireSodium"/> — the Edwards-to-Montgomery conversion
/// cross-checked through both routes against the real native library.
/// </summary>
[TestClass]
public sealed class LibsodiumKeyConversionTests
{
    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void PublicKeyConversionValidatesArguments()
    {
        //Argument validation precedes the initialization gate (see LibsodiumKeyConversion), so this
        //holds on hosts without libsodium and runs unconditionally.
        using var pool = new BaseMemoryPool();

        Assert.ThrowsExactly<ArgumentNullException>(
            () => LibsodiumKeyConversion.ConvertEd25519PublicKeyToCurve25519PublicKey(new byte[32], null!));
        Assert.ThrowsExactly<ArgumentException>(
            () => LibsodiumKeyConversion.ConvertEd25519PublicKeyToCurve25519PublicKey(new byte[31], pool));
    }


    [TestMethod]
    public void PrivateKeyConversionValidatesArguments()
    {
        //Argument validation precedes the initialization gate (see LibsodiumKeyConversion), so this
        //holds on hosts without libsodium and runs unconditionally.
        using var pool = new BaseMemoryPool();
        using var scratchPool = new BaseMemoryPool();

        Assert.ThrowsExactly<ArgumentNullException>(
            () => LibsodiumKeyConversion.ConvertEd25519PrivateKeyToCurve25519PrivateKey(new byte[32], null!, scratchPool));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => LibsodiumKeyConversion.ConvertEd25519PrivateKeyToCurve25519PrivateKey(new byte[32], pool, null!));
        Assert.ThrowsExactly<ArgumentException>(
            () => LibsodiumKeyConversion.ConvertEd25519PrivateKeyToCurve25519PrivateKey(new byte[33], pool, scratchPool));
    }


    [TestMethod]
    public void ConversionRoutesAgree()
    {
        LibsodiumTestEnvironment.RequireSodium();

        //The public-key route (pk -> curve pk) and the private-key route (seed -> curve sk ->
        //scalarmult_base) must land on the same X25519 public key; agreement proves both conversions
        //against each other with no fixture beyond a random seed.
        Span<byte> seed = stackalloc byte[LibsodiumCrypto.Ed25519SeedLength];
        LibsodiumCrypto.RandomBytes(seed);

        using var pool = new BaseMemoryPool();
        using var scratchPool = new BaseMemoryPool();

        using var scratchOwner = LibsodiumCrypto.AllocateSecretKeyScratch(scratchPool, "Scratch allocation failed in test.");
        using var scratchHandle = scratchOwner.Memory.Pin();

        nint scratch;
        unsafe
        {
            scratch = (nint)scratchHandle.Pointer;
        }

        Span<byte> ed25519PublicKey = stackalloc byte[LibsodiumCrypto.Ed25519PublicKeyLength];
        Assert.AreEqual(0, LibsodiumCrypto.SignSeedKeypair(ed25519PublicKey, scratch, seed), "crypto_sign_seed_keypair should succeed.");

        using var curvePublicKeyOwner = LibsodiumKeyConversion.ConvertEd25519PublicKeyToCurve25519PublicKey(ed25519PublicKey, pool);
        using var curvePrivateKeyOwner = LibsodiumKeyConversion.ConvertEd25519PrivateKeyToCurve25519PrivateKey(seed, pool, scratchPool);

        Assert.HasCount(LibsodiumCrypto.X25519PointLength, curvePublicKeyOwner.Memory,
            "The converted public key must be exactly one X25519 point.");
        Assert.HasCount(LibsodiumCrypto.X25519ScalarLength, curvePrivateKeyOwner.Memory,
            "The converted private key must be exactly one X25519 scalar.");

        Span<byte> derivedPublicKey = stackalloc byte[LibsodiumCrypto.X25519PointLength];
        Assert.AreEqual(0, LibsodiumCrypto.ScalarMultBase(derivedPublicKey, curvePrivateKeyOwner.Memory.Span), "crypto_scalarmult_base should succeed.");

        Assert.AreSequenceEqual(curvePublicKeyOwner.Memory.Span, derivedPublicKey,
            "Both conversion routes must land on the same X25519 public key.");
    }
}
