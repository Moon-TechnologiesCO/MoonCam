pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "MoonCam"

// Pure Kotlin: builds and tests with just a JDK.
include(":protocol")

// The Android app needs the Android SDK. Include it only where one is configured, so the protocol module
// can still be built and tested on machines (and CI jobs) without the SDK.
if (System.getenv("ANDROID_HOME") != null || file("local.properties").exists()) {
    include(":app")
}
