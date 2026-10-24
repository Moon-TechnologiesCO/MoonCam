package io.github.moontechnologiesco.mooncam.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import io.github.moontechnologiesco.mooncam.R

/** Moon Technologies palette (brand/BRAND.md). */
object MoonColors {
    val Night = Color(0xFF101633)
    val DeepViolet = Color(0xFF1F1752)
    val Moonlight = Color(0xFFFFF8E7)
    val Mist = Color(0xFFC9CFF5)
    val Sky = Color(0xFF4F86FF)
    val Sky600 = Color(0xFF2F5FD0)
    val Violet = Color(0xFF9B5CFF)
    val Glass = Color(0x14FFFFFF)
    val GlassBorder = Color(0x22FFFFFF)
    val Live = Color(0xFFFF5F57)
    val Glow = Color(0xFFFFE9A8)
}

val MoonSans = FontFamily(
    Font(R.font.moon_sans_regular, FontWeight.Normal),
    Font(R.font.moon_sans_medium, FontWeight.Medium),
    Font(R.font.moon_sans_semibold, FontWeight.SemiBold),
)

val MoonMono = FontFamily(Font(R.font.moon_mono_regular, FontWeight.Normal))

private fun Typography.withFamily(family: FontFamily): Typography {
    fun TextStyle.f() = copy(fontFamily = family)
    return Typography(
        displayLarge.f(), displayMedium.f(), displaySmall.f(),
        headlineLarge.f(), headlineMedium.f(), headlineSmall.f(),
        titleLarge.f(), titleMedium.f(), titleSmall.f(),
        bodyLarge.f(), bodyMedium.f(), bodySmall.f(),
        labelLarge.f(), labelMedium.f(), labelSmall.f(),
    )
}

@Composable
fun MoonCamTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(
            primary = MoonColors.Sky600,
            onPrimary = Color.White,
            secondary = MoonColors.Violet,
            background = MoonColors.Night,
            onBackground = MoonColors.Moonlight,
            surface = MoonColors.DeepViolet,
            onSurface = MoonColors.Moonlight,
            onSurfaceVariant = MoonColors.Mist,
        ),
        typography = Typography().withFamily(MoonSans),
    ) {
        // Without a Surface, Compose text falls back to black. This one gives every screen moonlight text.
        Surface(color = MoonColors.Night, contentColor = MoonColors.Moonlight, content = content)
    }
}
