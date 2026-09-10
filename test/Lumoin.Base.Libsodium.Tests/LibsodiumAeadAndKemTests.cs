using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lumoin.Base.Libsodium.Tests;

/// <summary>
/// Behavioral tests for the XChaCha20-Poly1305 AEAD and the ML-KEM-768 / X-Wing KEM surface:
/// argument validation that holds regardless of whether libsodium is present, and — gated behind
/// <see cref="LibsodiumTestEnvironment.RequireSodium"/> — round-trips, tamper rejection, and the
/// FIPS 203 implicit-rejection contract against the real native library.
/// </summary>
[TestClass]
public sealed class LibsodiumAeadAndKemTests
{
    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void AllocateSecretScratchValidatesArguments()
    {
        //Argument validation precedes the initialization gate (see LibsodiumCrypto), so this holds
        //on hosts without libsodium and runs unconditionally.
        using var pool = new BaseMemoryPool();

        Assert.ThrowsExactly<ArgumentNullException>(() => LibsodiumCrypto.AllocateSecretScratch(null!, 32, "message"));
        Assert.ThrowsExactly<ArgumentNullException>(() => LibsodiumCrypto.AllocateSecretScratch(pool, 32, null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LibsodiumCrypto.AllocateSecretScratch(pool, 0, "message"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LibsodiumCrypto.AllocateSecretScratch(pool, -1, "message"));
    }


    [TestMethod]
    public void AeadRoundTripsAndAuthenticates()
    {
        LibsodiumTestEnvironment.RequireSodium();

        Span<byte> key = stackalloc byte[LibsodiumCrypto.XChaCha20Poly1305KeyLength];
        Span<byte> nonce = stackalloc byte[LibsodiumCrypto.XChaCha20Poly1305NonceLength];
        LibsodiumCrypto.RandomBytes(key);
        LibsodiumCrypto.RandomBytes(nonce);

        ReadOnlySpan<byte> message = "The family builds its own native assets."u8;
        ReadOnlySpan<byte> associatedData = "header"u8;

        Span<byte> ciphertext = stackalloc byte[message.Length + LibsodiumCrypto.XChaCha20Poly1305TagLength];
        Assert.AreEqual(0, LibsodiumCrypto.AeadXChaCha20Poly1305Encrypt(ciphertext, message, associatedData, nonce, key),
            "Encryption should succeed.");
        Assert.IsFalse(ciphertext[..message.Length].SequenceEqual(message), "Ciphertext must not equal the plaintext.");

        Span<byte> decrypted = stackalloc byte[message.Length];
        Assert.AreEqual(0, LibsodiumCrypto.AeadXChaCha20Poly1305Decrypt(decrypted, ciphertext, associatedData, nonce, key),
            "Decryption with the right key, nonce and associated data should succeed.");
        Assert.AreSequenceEqual(message, decrypted, "The round-tripped plaintext must match.");
    }


    [TestMethod]
    public void AeadRejectsTamperedCiphertextAssociatedDataAndNonce()
    {
        LibsodiumTestEnvironment.RequireSodium();

        Span<byte> key = stackalloc byte[LibsodiumCrypto.XChaCha20Poly1305KeyLength];
        Span<byte> nonce = stackalloc byte[LibsodiumCrypto.XChaCha20Poly1305NonceLength];
        LibsodiumCrypto.RandomBytes(key);
        LibsodiumCrypto.RandomBytes(nonce);

        ReadOnlySpan<byte> message = "tamper detection"u8;
        ReadOnlySpan<byte> associatedData = "header"u8;

        Span<byte> ciphertext = stackalloc byte[message.Length + LibsodiumCrypto.XChaCha20Poly1305TagLength];
        Assert.AreEqual(0, LibsodiumCrypto.AeadXChaCha20Poly1305Encrypt(ciphertext, message, associatedData, nonce, key),
            "Encryption should succeed.");

        Span<byte> decrypted = stackalloc byte[message.Length];

        ciphertext[0] ^= 0x01;
        Assert.AreNotEqual(0, LibsodiumCrypto.AeadXChaCha20Poly1305Decrypt(decrypted, ciphertext, associatedData, nonce, key),
            "A tampered ciphertext must not decrypt.");
        ciphertext[0] ^= 0x01;

        Assert.AreNotEqual(0, LibsodiumCrypto.AeadXChaCha20Poly1305Decrypt(decrypted, ciphertext, "other"u8, nonce, key),
            "Mismatched associated data must not decrypt.");

        Span<byte> otherNonce = stackalloc byte[LibsodiumCrypto.XChaCha20Poly1305NonceLength];
        nonce.CopyTo(otherNonce);
        otherNonce[0] ^= 0x01;
        Assert.AreNotEqual(0, LibsodiumCrypto.AeadXChaCha20Poly1305Decrypt(decrypted, ciphertext, associatedData, otherNonce, key),
            "A different nonce must not decrypt.");

        //The untampered inputs still decrypt, proving the rejections tested the tampering, not the setup.
        Assert.AreEqual(0, LibsodiumCrypto.AeadXChaCha20Poly1305Decrypt(decrypted, ciphertext, associatedData, nonce, key),
            "The original inputs must still decrypt.");
    }


    [TestMethod]
    public void MlKem768RoundTripsWithImplicitRejection()
    {
        LibsodiumTestEnvironment.RequireSodium();

        using var scratchPool = new BaseMemoryPool();
        using var secretKeyOwner = LibsodiumCrypto.AllocateSecretScratch(
            scratchPool, LibsodiumCrypto.MlKem768SecretKeyLength, "ML-KEM secret key scratch failed in test.");
        using var secretKeyHandle = secretKeyOwner.Memory.Pin();

        nint secretKey;
        unsafe
        {
            secretKey = (nint)secretKeyHandle.Pointer;
        }

        //Deterministic derivation: the same seed must yield the same public key twice.
        Span<byte> seed = stackalloc byte[LibsodiumCrypto.MlKem768SeedLength];
        LibsodiumCrypto.RandomBytes(seed);
        byte[] publicKey = new byte[LibsodiumCrypto.MlKem768PublicKeyLength];
        byte[] publicKeyAgain = new byte[LibsodiumCrypto.MlKem768PublicKeyLength];
        Assert.AreEqual(0, LibsodiumCrypto.MlKem768SeedKeypair(publicKey, secretKey, seed), "Seed keypair derivation should succeed.");
        Assert.AreEqual(0, LibsodiumCrypto.MlKem768SeedKeypair(publicKeyAgain, secretKey, seed), "Repeated derivation should succeed.");
        Assert.AreSequenceEqual(publicKeyAgain, publicKey, "Seed keypair derivation must be deterministic.");

        Span<byte> ciphertext = stackalloc byte[LibsodiumCrypto.MlKem768CiphertextLength];
        Span<byte> encapsulated = stackalloc byte[LibsodiumCrypto.MlKem768SharedSecretLength];
        Assert.AreEqual(0, LibsodiumCrypto.MlKem768Encapsulate(ciphertext, encapsulated, publicKey), "Encapsulation should succeed.");

        Span<byte> decapsulated = stackalloc byte[LibsodiumCrypto.MlKem768SharedSecretLength];
        Assert.AreEqual(0, LibsodiumCrypto.MlKem768Decapsulate(decapsulated, ciphertext, secretKey), "Decapsulation should succeed.");
        Assert.AreSequenceEqual(encapsulated, decapsulated, "Both sides must derive the same shared secret.");

        //FIPS 203 implicit rejection: a tampered ciphertext still decapsulates with return code 0,
        //but the derived secret must silently differ from the encapsulator's.
        ciphertext[0] ^= 0x01;
        Span<byte> rejected = stackalloc byte[LibsodiumCrypto.MlKem768SharedSecretLength];
        Assert.AreEqual(0, LibsodiumCrypto.MlKem768Decapsulate(rejected, ciphertext, secretKey),
            "Implicit rejection still returns 0 for a tampered ciphertext.");
        Assert.IsFalse(rejected.SequenceEqual(encapsulated),
            "A tampered ciphertext must yield a different (pseudorandom) shared secret.");
    }


    [TestMethod]
    public void XWingRoundTripsWithImplicitRejection()
    {
        LibsodiumTestEnvironment.RequireSodium();

        using var scratchPool = new BaseMemoryPool();
        using var secretKeyOwner = LibsodiumCrypto.AllocateSecretScratch(
            scratchPool, LibsodiumCrypto.XWingSecretKeyLength, "X-Wing secret key scratch failed in test.");
        using var secretKeyHandle = secretKeyOwner.Memory.Pin();

        nint secretKey;
        unsafe
        {
            secretKey = (nint)secretKeyHandle.Pointer;
        }

        Span<byte> seed = stackalloc byte[LibsodiumCrypto.XWingSeedLength];
        LibsodiumCrypto.RandomBytes(seed);
        byte[] publicKey = new byte[LibsodiumCrypto.XWingPublicKeyLength];
        byte[] publicKeyAgain = new byte[LibsodiumCrypto.XWingPublicKeyLength];
        Assert.AreEqual(0, LibsodiumCrypto.XWingSeedKeypair(publicKey, secretKey, seed), "Seed keypair derivation should succeed.");
        Assert.AreEqual(0, LibsodiumCrypto.XWingSeedKeypair(publicKeyAgain, secretKey, seed), "Repeated derivation should succeed.");
        Assert.AreSequenceEqual(publicKeyAgain, publicKey, "Seed keypair derivation must be deterministic.");

        Span<byte> ciphertext = stackalloc byte[LibsodiumCrypto.XWingCiphertextLength];
        Span<byte> encapsulated = stackalloc byte[LibsodiumCrypto.XWingSharedSecretLength];
        Assert.AreEqual(0, LibsodiumCrypto.XWingEncapsulate(ciphertext, encapsulated, publicKey), "Encapsulation should succeed.");

        Span<byte> decapsulated = stackalloc byte[LibsodiumCrypto.XWingSharedSecretLength];
        Assert.AreEqual(0, LibsodiumCrypto.XWingDecapsulate(decapsulated, ciphertext, secretKey), "Decapsulation should succeed.");
        Assert.AreSequenceEqual(encapsulated, decapsulated, "Both sides must derive the same shared secret.");

        ciphertext[0] ^= 0x01;
        Span<byte> rejected = stackalloc byte[LibsodiumCrypto.XWingSharedSecretLength];
        Assert.AreEqual(0, LibsodiumCrypto.XWingDecapsulate(rejected, ciphertext, secretKey),
            "Implicit rejection still returns 0 for a tampered ciphertext.");
        Assert.IsFalse(rejected.SequenceEqual(encapsulated),
            "A tampered ciphertext must yield a different (pseudorandom) shared secret.");
    }
}
