package io.github.moontechnologiesco.mooncam.protocol

import java.nio.ByteBuffer
import java.security.MessageDigest
import java.security.SecureRandom

/**
 * The Wi-Fi pairing code (docs/security.md §Pairing). The phone commits to its nonce before it sees the
 * desktop's, so a man in the middle cannot choose values that make both screens show the same six digits.
 * Must match the desktop implementation byte for byte (shared vector in protocol/test-vectors).
 */
object Pairing {
    const val NONCE_SIZE = 32
    private val label = "MoonCam pairing v1".toByteArray(Charsets.US_ASCII)
    private val random = SecureRandom()

    fun newNonce(): ByteArray = ByteArray(NONCE_SIZE).also(random::nextBytes)

    fun commit(nonce: ByteArray): ByteArray = sha256(nonce)

    fun code(phoneFingerprint: ByteArray, desktopFingerprint: ByteArray, phoneNonce: ByteArray, desktopNonce: ByteArray): String {
        val digest = MessageDigest.getInstance("SHA-256").apply {
            update(label)
            update(phoneFingerprint)
            update(desktopFingerprint)
            update(phoneNonce)
            update(desktopNonce)
        }.digest()
        val value = (ByteBuffer.wrap(digest, 0, 4).int.toLong() and 0xFFFFFFFFL) % 1_000_000
        return "%06d".format(value)
    }

    /** SHA-256 of a DER-encoded certificate: the identity that pairing pins. */
    fun fingerprint(certificateDer: ByteArray): ByteArray = sha256(certificateDer)

    fun format(code: String) = if (code.length == 6) "${code.take(3)} ${code.drop(3)}" else code

    private fun sha256(data: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(data)
}
