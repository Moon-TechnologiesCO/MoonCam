package io.github.moontechnologiesco.mooncam.protocol

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.TestFactory
import org.junit.jupiter.api.assertThrows
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.File

/** Runs the shared MoonLink vectors (protocol/test-vectors), the same ones the desktop tests run. */
class TestVectorsTest {
    private val vectors: JsonObject =
        Json.parseToJsonElement(File(System.getProperty("mooncam.vectors")).readText()).jsonObject

    @Test
    fun `preamble matches the vector`() {
        val expected = hex(vectors["preamble"]!!.jsonObject["hex"]!!.jsonPrimitive.content)
        assertArrayEquals(expected, Preamble.encode())
        assertEquals(1, Preamble.parse(expected))
    }

    @TestFactory
    fun `valid frames decode and re-encode identically`() = group("valid").map { vector ->
        DynamicTest.dynamicTest(vector.name()) {
            val bytes = hex(vector["hex"]!!.jsonPrimitive.content)
            val frame = FrameReader(ByteArrayInputStream(bytes)).read()!!

            assertEquals(vector["type"]!!.jsonPrimitive.int, frame.type.code)
            assertEquals(vector["flags"]!!.jsonPrimitive.int, frame.flags)
            assertEquals(vector["timestampUs"]!!.jsonPrimitive.long, frame.timestampUs)
            assertArrayEquals(hex(vector["payloadHex"]!!.jsonPrimitive.content), frame.payload)

            val out = ByteArrayOutputStream()
            FrameWriter(out).write(frame)
            assertArrayEquals(bytes, out.toByteArray())
        }
    }

    @TestFactory
    fun `invalid frames are rejected`() = group("invalid").map { vector ->
        DynamicTest.dynamicTest(vector.name()) {
            val bytes = hex(vector["hex"]!!.jsonPrimitive.content)
            val error = runCatching { FrameReader(ByteArrayInputStream(bytes)).read() }.exceptionOrNull()
            assertTrue(error is ProtocolException || error is EOFException, "${vector.name()}: got $error")
        }
    }

    @Test
    fun `video config vector decodes`() {
        val vector = group("valid").single { it.name().startsWith("video config") }
        val config = VideoConfig.fromFrame(FrameReader(ByteArrayInputStream(hex(vector["hex"]!!.jsonPrimitive.content))).read()!!)
        val expected = vector["videoConfig"]!!.jsonObject

        assertEquals(expected["width"]!!.jsonPrimitive.int, config.width)
        assertEquals(expected["height"]!!.jsonPrimitive.int, config.height)
        assertEquals(expected["fps"]!!.jsonPrimitive.int, config.fps)
        assertArrayEquals(hex(expected["codecDataHex"]!!.jsonPrimitive.content), config.codecData)
        assertArrayEquals(hex(vector["payloadHex"]!!.jsonPrimitive.content), config.toFrame().payload)
    }

    @Test
    fun `json vectors decode`() {
        for (vector in group("json")) {
            val frame = Frame(FrameType.fromCode(vector["type"]!!.jsonPrimitive.int), vector["payload"].toString().toByteArray())
            when (frame.type) {
                FrameType.HELLO -> assertEquals("abc123", Messages.readHello(frame).token)
                FrameType.WELCOME -> assertTrue(Messages.readWelcome(frame).accepted)
                FrameType.CONTROL -> assertEquals(2_800_000, Messages.readControl(frame).bitrateBps)
                else -> error("Unexpected vector ${frame.type}")
            }
            assertTrue(vector["payload"]!!.jsonObject.isNotEmpty())
        }
    }

    @Test
    fun `control messages omit null fields`() {
        val frame = Messages.control(ControlMessage(ControlOps.KEY_FRAME))
        assertEquals("""{"op":"keyFrame"}""", frame.payload.toString(Charsets.UTF_8))
    }

    @Test
    fun `clean end of stream between frames returns null`() {
        assertNull(FrameReader(ByteArrayInputStream(ByteArray(0))).read())
    }

    @Test
    fun `bad preamble is rejected`() {
        assertThrows<ProtocolException> { Preamble.parse("GET / HTTP/1.".toByteArray()) }
    }

    @Test
    fun `welcome with unknown fields is still accepted`() {
        val frame = Frame(FrameType.WELCOME, """{"accepted":true,"serverName":"pc","future":1}""".toByteArray())
        assertTrue(Messages.readWelcome(frame).accepted)
    }

    private fun group(name: String) = vectors[name]!!.jsonArray.map { it.jsonObject }

    private fun JsonObject.name() = this["name"]!!.jsonPrimitive.content

    @Suppress("unused")
    private fun JsonObject.flag(name: String) = this[name]!!.jsonPrimitive.boolean

    private fun hex(text: String): ByteArray {
        val clean = text.replace(" ", "")
        return ByteArray(clean.length / 2) { i -> clean.substring(i * 2, i * 2 + 2).toInt(16).toByte() }
    }
}
