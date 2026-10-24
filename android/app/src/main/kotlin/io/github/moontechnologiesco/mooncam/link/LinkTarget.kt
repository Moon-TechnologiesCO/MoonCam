package io.github.moontechnologiesco.mooncam.link

import io.github.moontechnologiesco.mooncam.protocol.Pairing
import java.io.Closeable
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngine
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509ExtendedTrustManager

/** Where the desktop is, and how each side proves who it is. */
sealed interface LinkTarget {
    /** The adb tunnel on 127.0.0.1. The desktop's one-time token proves it invited this phone. */
    data class Usb(val port: Int, val token: String) : LinkTarget

    /**
     * The local network, over TLS 1.3 with a certificate on both sides. With [desktopFingerprintHex] set only
     * that computer is accepted (it was paired before); without it the phone asks to pair, and the six-digit
     * code shows both people that no one is in the middle.
     */
    data class WiFi(val computer: String, val host: String, val port: Int, val desktopFingerprintHex: String?) : LinkTarget {
        val isPairing: Boolean get() = desktopFingerprintHex == null
    }
}

/** An open connection: the socket to talk on and, over Wi-Fi, the fingerprint of the desktop's certificate. */
class Connection(val socket: Socket, val desktopFingerprint: ByteArray?) : Closeable {
    override fun close() = socket.close()
}

/** The computer presented a different certificate from the one this phone paired with. */
class IdentityChangedException(val computer: String) : IOException("$computer presented an unknown certificate.")

/** Opens [LinkTarget]s on a socket the caller owns, so the caller can close it to cancel a blocked connect. */
object Connector {
    private const val CONNECT_TIMEOUT_MS = 3_000

    fun open(target: LinkTarget, raw: Socket): Connection = when (target) {
        is LinkTarget.Usb -> {
            raw.connect(InetSocketAddress(InetAddress.getLoopbackAddress(), target.port), CONNECT_TIMEOUT_MS)
            Connection(raw, null)
        }
        is LinkTarget.WiFi -> openTls(target, raw)
    }

    private fun openTls(target: LinkTarget.WiFi, raw: Socket): Connection {
        val trust = DesktopTrust(target.desktopFingerprintHex?.let(::hexToBytes))
        val tls = SSLContext.getInstance("TLSv1.3").apply { init(arrayOf(PhoneIdentity.keyManager), arrayOf(trust), null) }
        raw.connect(InetSocketAddress(target.host, target.port), CONNECT_TIMEOUT_MS)
        val socket = tls.socketFactory.createSocket(raw, target.host, target.port, true) as SSLSocket
        socket.enabledProtocols = arrayOf("TLSv1.3")
        try {
            socket.startHandshake()
        } catch (e: IOException) {
            if (trust.mismatch) throw IdentityChangedException(target.computer)
            throw e
        }
        return Connection(socket, trust.seen ?: throw IOException("The computer sent no certificate."))
    }

    private fun hexToBytes(hex: String) = ByteArray(hex.length / 2) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
}

/**
 * Trusts the desktop by its certificate's fingerprint, not by a certificate authority: a local computer has no
 * public name to certify. TLS itself proves the desktop holds the certificate's key; this checks which key.
 * While pairing ([pinned] is null) any certificate is accepted and recorded, and the pairing code covers it.
 */
private class DesktopTrust(private val pinned: ByteArray?) : X509ExtendedTrustManager() {
    @Volatile var seen: ByteArray? = null
        private set

    @Volatile var mismatch = false
        private set

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val certificate = chain?.firstOrNull() ?: throw CertificateException("The computer sent no certificate.")
        val fingerprint = Pairing.fingerprint(certificate.encoded)
        if (pinned != null && !MessageDigest.isEqual(fingerprint, pinned)) {
            mismatch = true
            throw CertificateException("This is not the computer this phone paired with.")
        }
        seen = fingerprint
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?, socket: Socket?) =
        checkServerTrusted(chain, authType)

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?, engine: SSLEngine?) =
        checkServerTrusted(chain, authType)

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = notAServer()

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?, socket: Socket?) = notAServer()

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?, engine: SSLEngine?) = notAServer()

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

    private fun notAServer(): Nothing = throw CertificateException("The phone never accepts connections.")
}

fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }
