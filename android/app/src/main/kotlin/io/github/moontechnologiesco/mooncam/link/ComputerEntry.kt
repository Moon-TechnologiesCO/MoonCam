package io.github.moontechnologiesco.mooncam.link

/** A computer as the home screen lists it: on this network, paired, or both. */
data class ComputerEntry(val name: String, val nearby: NearbyComputer?, val paired: PairedComputer?) {
    /** A paired computer is pinned by its certificate; a new one is asked to pair. */
    fun target(): LinkTarget.WiFi = LinkTarget.WiFi(
        computer = name,
        host = nearby?.host ?: paired!!.host,
        port = nearby?.port ?: paired!!.port,
        desktopFingerprintHex = paired?.fingerprintHex,
    )

    companion object {
        /** Paired computers first (those on this network before the rest), then new ones to pair with. */
        fun merge(nearby: List<NearbyComputer>, paired: List<PairedComputer>): List<ComputerEntry> {
            val known = paired.map { p -> ComputerEntry(p.name, nearby.firstOrNull { it.isSameAs(p) }, p) }
                .sortedByDescending { it.nearby != null }
            val new = nearby.filter { n -> paired.none(n::isSameAs) }.map { ComputerEntry(it.name, it, null) }
            return known + new
        }
    }
}
