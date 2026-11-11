plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

android {
    namespace = "io.github.moontechnologiesco.mooncam"
    compileSdk = 37

    defaultConfig {
        applicationId = "io.github.moontechnologiesco.mooncam"
        minSdk = 29 // Android 10: Opus encoder, microphone direction hints, modern camera APIs
        targetSdk = 37
        versionCode = 6
        versionName = "0.6.0"
        // MediaPipe ships native code: phones only (64- and 32-bit ARM), which keeps the APK about 20 MB smaller.
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a") }
    }

    androidResources {
        noCompress += "tflite" // MediaPipe maps the model straight from the APK
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
    }
}

dependencies {
    implementation(project(":protocol"))
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.service)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.mediapipe.tasks.vision) // person segmentation for background effects (Apache-2.0)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.material3)
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.tooling.preview)
}
