package io.github.moontechnologiesco.mooncam.link

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject

/** A computer this phone trusts over Wi-Fi: its certificate fingerprint and where it was last seen. */
data class PairedComputer(val name: String, val fingerprintHex: String, val host: String, val port: Int)

/**
 * The computers this phone paired with, kept in app-private storage that is never backed up (see
 * data_extraction_rules.xml). Fingerprints are not secret, but which computers are trusted must not be editable
 * by other apps. One instance per process, so the screen and the streaming service see the same list.
 */
class PairedComputers private constructor(context: Context) {
    private val prefs = context.getSharedPreferences("paired-computers", Context.MODE_PRIVATE)
    private val mutable = MutableStateFlow(load())
    val all: StateFlow<List<PairedComputer>> = mutable.asStateFlow()

    fun find(fingerprintHex: String): PairedComputer? = mutable.value.firstOrNull { it.fingerprintHex == fingerprintHex }

    fun save(computer: PairedComputer) = update { list -> list.filterNot { it.fingerprintHex == computer.fingerprintHex } + computer }

    fun remove(fingerprintHex: String) = update { list -> list.filterNot { it.fingerprintHex == fingerprintHex } }

    /** Remembers where a paired computer was last seen, so reconnecting still finds it after its address changes. */
    fun seenAt(fingerprintHex: String, host: String, port: Int) {
        val known = find(fingerprintHex) ?: return
        if (known.host != host || known.port != port) save(known.copy(host = host, port = port))
    }

    @Synchronized
    private fun update(change: (List<PairedComputer>) -> List<PairedComputer>) {
        val next = change(mutable.value)
        val json = JSONArray(next.map {
            JSONObject().put("name", it.name).put("fingerprint", it.fingerprintHex).put("host", it.host).put("port", it.port)
        })
        prefs.edit().putString(KEY, json.toString()).apply()
        mutable.value = next
    }

    private fun load(): List<PairedComputer> {
        val json = JSONArray(prefs.getString(KEY, "[]"))
        return (0 until json.length()).map { i ->
            val o = json.getJSONObject(i)
            PairedComputer(o.getString("name"), o.getString("fingerprint"), o.getString("host"), o.getInt("port"))
        }
    }

    companion object {
        private const val KEY = "computers"

        @Volatile private var instance: PairedComputers? = null

        fun get(context: Context): PairedComputers =
            instance ?: synchronized(this) { instance ?: PairedComputers(context.applicationContext).also { instance = it } }
    }
}
