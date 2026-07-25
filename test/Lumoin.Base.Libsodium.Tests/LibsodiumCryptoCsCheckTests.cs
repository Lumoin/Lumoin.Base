using CsCheck;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Libsodium.Tests;

/// <summary>
/// Property-based tests using CsCheck for <see cref="LibsodiumCrypto"/>, gated behind
/// <see cref="LibsodiumTestEnvironment.RequireSodium"/> since every case exercises the real native
/// library.
/// </summary>
[TestClass]
public sealed class LibsodiumCryptoCsCheckTests
{
    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void PropertySignVerifyRoundTripsAndRejectsBitFlips()
    {
        LibsodiumTestEnvironment.RequireSodium();

        //Any seed and any message must sign-and-verify, and flipping any single signature bit must
        //fail verification: Ed25519 signatures carry no per-bit slack for this to hide in.
        Gen.Select(
            Gen.Byte.Array[LibsodiumCrypto.Ed25519SeedLength],
            Gen.Byte.Array[0, 256],
            Gen.Int[0, LibsodiumCrypto.Ed25519SignatureLength * 8 - 1])
        .Sample((seed, message, signatureBitToFlip) =>
        {
            using var scratchPool = new BaseMemoryPool();
            using var scratchOwner = LibsodiumCrypto.AllocateSecretKeyScratch(scratchPool, "Scratch allocation failed in test.");
            using var scratchHandle = scratchOwner.Memory.Pin();

            nint scratch;
            unsafe
            {
                scratch = (nint)scratchHandle.Pointer;
            }

            Span<byte> publicKey = stackalloc byte[LibsodiumCrypto.Ed25519PublicKeyLength];
            Assert.AreEqual(0, LibsodiumCrypto.SignSeedKeypair(publicKey, scratch, seed), "crypto_sign_seed_keypair should succeed.");

            Span<byte> signature = stackalloc byte[LibsodiumCrypto.Ed25519SignatureLength];
            Assert.AreEqual(0, LibsodiumCrypto.SignDetached(signature, message, scratch), "crypto_sign_detached should succeed.");
            Assert.AreEqual(0, LibsodiumCrypto.VerifyDetached(signature, message, publicKey), "A fresh signature must verify.");

            signature[signatureBitToFlip / 8] ^= (byte)(1 << (signatureBitToFlip % 8));
            Assert.AreNotEqual(0, LibsodiumCrypto.VerifyDetached(signature, message, publicKey),
                $"A signature with bit {signatureBitToFlip} flipped must not verify.");
        });
    }
}
