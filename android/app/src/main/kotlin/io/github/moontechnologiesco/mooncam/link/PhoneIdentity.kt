package io.github.moontechnologiesco.mooncam.link

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Log
import io.github.moontechnologiesco.mooncam.protocol.Pairing
import java.math.BigInteger
import java.net.Socket
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.Principal
import java.security.PrivateKey
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.util.Date
import javax.net.ssl.SSLEngine
import javax.net.ssl.X509ExtendedKeyManager
import javax.security.auth.x500.X500Principal

/**
 * The phone's TLS identity for Wi-Fi: an EC P-256 key that lives in the Android Keystore (it can sign, but it
 * can never be read out of the device) and the self-signed certificate the Keystore issues for it. The desktop
 * pins this certificate's fingerprint when the phone is paired.
 */
object PhoneIdentity {
    private const val ALIAS = "mooncam-identity"
    private val keyStore: KeyStore by lazy { KeyStore.getInstance("AndroidKeyStore").apply { load(null) } }

    @get:Synchronized
    val certificate: X509Certificate
        get() {
            if (!keyStore.containsAlias(ALIAS)) generate()
            return keyStore.getCertificate(ALIAS) as X509Certificate
        }

    val fingerprint: ByteArray get() = Pairing.fingerprint(certificate.encoded)

    /** Presents the identity during the TLS handshake. */
    val keyManager: X509ExtendedKeyManager = object : X509ExtendedKeyManager() {
        override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?) = ALIAS
        override fun chooseEngineClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, engine: SSLEngine?) = ALIAS
        override fun getCertificateChain(alias: String?): Array<X509Certificate> = arrayOf(certificate)
        override fun getPrivateKey(alias: String?): PrivateKey = keyStore.getKey(ALIAS, null) as PrivateKey
        override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?) = arrayOf(ALIAS)
        override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?) = emptyArray<String>()
        override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?): String? = null
    }

    /** Diagnostics after a failed TLS handshake: can the identity key sign the way TLS asks it to? */
    fun logSelfTest() {
        val key = runCatching { keyStore.getKey(ALIAS, null) as PrivateKey }
        Log.i("MoonCam", "Identity: key=${key.getOrNull()?.javaClass?.name} cert=${runCatching { certificate.sigAlgName }.getOrElse { it.toString() }}")
        for (algorithm in listOf("NONEwithECDSA", "SHA256withECDSA")) {
            val result = runCatching {
                java.security.Signature.getInstance(algorithm).run {
                    initSign(key.getOrThrow())
                    update(ByteArray(32) { it.toByte() })
                    sign().size
                }
            }
            Log.i("MoonCam", "Identity self-test $algorithm: ${result.fold({ "ok, $it bytes" }, { Log.getStackTraceString(it) })}")
        }
    }

    private fun generate() {
        val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
        generator.initialize(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
                .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_NONE)
                .setCertificateSubject(X500Principal("CN=MoonCam Phone"))
                .setCertificateSerialNumber(BigInteger.ONE)
                .setCertificateNotBefore(Date(System.currentTimeMillis() - 86_400_000L))
                .setCertificateNotAfter(Date(System.currentTimeMillis() + 20L * 365 * 86_400_000L))
                .build(),
        )
        generator.generateKeyPair()
    }
}
