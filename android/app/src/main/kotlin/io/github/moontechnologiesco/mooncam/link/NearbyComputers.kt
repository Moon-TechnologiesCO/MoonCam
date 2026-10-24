package io.github.moontechnologiesco.mooncam.link

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.buffer
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull
import java.net.Inet4Address
import java.net.InetAddress
import java.util.concurrent.ConcurrentHashMap
import kotlin.coroutines.resume

/** A MoonCam desktop announcing itself on this network. [fingerprintPrefix] identifies paired computers. */
data class NearbyComputer(val name: String, val host: String, val port: Int, val fingerprintPrefix: String?) {
    fun isSameAs(paired: PairedComputer) =
        fingerprintPrefix != null && fingerprintPrefix.length >= 16 && paired.fingerprintHex.startsWith(fingerprintPrefix, ignoreCase = true)
}

/**
 * Finds MoonCam desktops on the local network through the system's mDNS service (`_mooncam._tcp`, see
 * docs/protocol.md). The announcement only says where a computer is: who it is is proven later by TLS.
 */
class NearbyComputers(context: Context) {
    private val nsd = context.applicationContext.getSystemService(NsdManager::class.java)

    /** The computers announcing themselves right now. Discovery runs only while this is collected. */
    val computers: Flow<List<NearbyComputer>> = callbackFlow {
        val found = ConcurrentHashMap<String, NearbyComputer>()
        val resolving = Mutex() // older Android versions resolve one service at a time
        fun publish() = trySend(found.values.sortedBy { it.name })

        val listener = object : NsdManager.DiscoveryListener {
            override fun onServiceFound(service: NsdServiceInfo) {
                launch {
                    val computer = resolving.withLock { withTimeoutOrNull(RESOLVE_TIMEOUT_MS) { resolve(service) } }
                    if (computer != null) {
                        found[service.serviceName] = computer
                        publish()
                    }
                }
            }

            override fun onServiceLost(service: NsdServiceInfo) {
                if (found.remove(service.serviceName) != null) publish()
            }

            override fun onDiscoveryStarted(serviceType: String) = Unit
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) = Unit // the list stays empty
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit
        }

        publish()
        nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener)
        awaitClose { runCatching { nsd.stopServiceDiscovery(listener) } }
    }.buffer(Channel.CONFLATED)

    @Suppress("DEPRECATION") // resolveService works on every supported version; its replacement needs Android 14
    private suspend fun resolve(service: NsdServiceInfo): NearbyComputer? = suspendCancellableCoroutine { continuation ->
        nsd.resolveService(service, object : NsdManager.ResolveListener {
            override fun onResolveFailed(service: NsdServiceInfo, errorCode: Int) = continuation.resume(null)

            override fun onServiceResolved(service: NsdServiceInfo) {
                val host = ipv4(service)?.hostAddress
                val fingerprint = service.attributes["fp"]?.toString(Charsets.US_ASCII)
                val name = service.serviceName.removePrefix("MoonCam on ")
                continuation.resume(host?.let { NearbyComputer(name, it, service.port, fingerprint) })
            }
        })
    }

    /** The desktop listens on IPv4, so prefer that address when the computer announces several. */
    @Suppress("DEPRECATION")
    private fun ipv4(service: NsdServiceInfo): InetAddress? {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            service.hostAddresses.firstOrNull { it is Inet4Address }?.let { return it }
        }
        return service.host?.takeIf { it is Inet4Address }
    }

    private companion object {
        const val SERVICE_TYPE = "_mooncam._tcp"
        const val RESOLVE_TIMEOUT_MS = 5_000L
    }
}
