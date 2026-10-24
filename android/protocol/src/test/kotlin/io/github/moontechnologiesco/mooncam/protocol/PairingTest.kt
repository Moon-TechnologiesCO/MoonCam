package io.github.moontechnologiesco.mooncam.protocol

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Test
import java.io.File

class PairingTest {
    private val vector = Json.parseToJsonElement(File(System.getProperty("mooncam.vectors")).readText())
        .jsonObject["pairing"]!!.jsonObject

    private fun hex(name: String): ByteArray {
        val text = vector[name]!!.jsonPrimitive.content
        return ByteArray(text.length / 2) { i -> text.substring(i * 2, i * 2 + 2).toInt(16).toByte() }
    }

    @Test
    fun `code and commit match the shared vector`() {
        assertArrayEquals(hex("commitHex"), Pairing.commit(hex("phoneNonceHex")))
        assertEquals(
            vector["code"]!!.jsonPrimitive.content,
            Pairing.code(hex("phoneFingerprintHex"), hex("desktopFingerprintHex"), hex("phoneNonceHex"), hex("desktopNonceHex")),
        )
    }

    @Test
    fun `a different desktop certificate gives a different code`() {
        val np = Pairing.newNonce()
        val nd = Pairing.newNonce()
        assertNotEquals(Pairing.code(ByteArray(32), ByteArray(32), np, nd), Pairing.code(ByteArray(32), ByteArray(32) { 1 }, np, nd))
        assertEquals("482 913", Pairing.format("482913"))
    }
}
