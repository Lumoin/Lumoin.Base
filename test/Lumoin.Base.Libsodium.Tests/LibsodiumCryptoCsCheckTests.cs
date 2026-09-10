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


    [TestMethod]
    public void PropertyAeadRoundTripsAndRejectsCiphertextBitFlips()
    {
        LibsodiumTestEnvironment.RequireSodium();

        //Any key, nonce, message and associated data must round-trip, and flipping any single bit
        //of the sealed output (ciphertext body or tag alike) must fail authentication.
        Gen.Select(
            Gen.Byte.Array[LibsodiumCrypto.XChaCha20Poly1305KeyLength],
            Gen.Byte.Array[LibsodiumCrypto.XChaCha20Poly1305NonceLength],
            Gen.Byte.Array[0, 256],
            Gen.Byte.Array[0, 64],
            Gen.Int[0, int.MaxValue])
        .Sample((key, nonce, message, associatedData, bitSeed) =>
        {
            Span<byte> ciphertext = stackalloc byte[message.Length + LibsodiumCrypto.XChaCha20Poly1305TagLength];
            Assert.AreEqual(0, LibsodiumCrypto.AeadXChaCha20Poly1305Encrypt(ciphertext, message, associatedData, nonce, key),
                "Encryption should succeed.");

            Span<byte> decrypted = stackalloc byte[message.Length];
            Assert.AreEqual(0, LibsodiumCrypto.AeadXChaCha20Poly1305Decrypt(decrypted, ciphertext, associatedData, nonce, key),
                "Decryption should succeed.");
            Assert.AreSequenceEqual(message.AsSpan(), decrypted, "The round-tripped plaintext must match.");

            int bitToFlip = bitSeed % (ciphertext.Length * 8);
            ciphertext[bitToFlip / 8] ^= (byte)(1 << (bitToFlip % 8));
            Assert.AreNotEqual(0, LibsodiumCrypto.AeadXChaCha20Poly1305Decrypt(decrypted, ciphertext, associatedData, nonce, key),
                $"Sealed output with bit {bitToFlip} flipped must not decrypt.");
        });
    }
}
