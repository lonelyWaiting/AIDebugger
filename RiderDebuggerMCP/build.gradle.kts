import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import org.jetbrains.kotlin.gradle.tasks.KotlinCompile
import java.util.concurrent.TimeUnit

plugins {
    kotlin("jvm") version "2.1.0"
    id("org.jetbrains.intellij.platform") version "2.11.0"
}

// ---------------------------------------------------------------------------
// Git-based version: tagged commit → tag version, else → 1.1.<commit count>
// Falls back to gradle.properties pluginVersion when git is unavailable.
// ---------------------------------------------------------------------------
fun gitVersion(): String {
    fun exec(vararg cmd: String): String? = try {
        val p = ProcessBuilder(*cmd).redirectErrorStream(true).start()
        val out = p.inputStream.bufferedReader().readText().trim()
        if (p.waitFor(5, TimeUnit.SECONDS) && p.exitValue() == 0) out else null
    } catch (_: Exception) { null }

    // Exact tag on HEAD? (e.g. v1.2.3)
    exec("git", "describe", "--exact-match", "--tags", "HEAD")?.let { tag ->
        Regex("^v?(\\d+\\.\\d+\\.\\d+)").find(tag)?.groupValues?.get(1)?.let { return it }
    }
    // Commit count
    exec("git", "rev-list", "--count", "HEAD")?.let { count ->
        if (count.all { it.isDigit() }) return "1.1.$count"
    }
    // Fallback
    return providers.gradleProperty("pluginVersion").get()
}

group = providers.gradleProperty("pluginGroup").get()
version = gitVersion()

val buildId = providers.environmentVariable("AIDB_GRADLE_BUILD_ID").orNull
if (!buildId.isNullOrBlank()) {
    layout.buildDirectory.set(file("build-$buildId"))
}

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

sourceSets {
    main {
        resources {
            setSrcDirs(listOf("src/main/resources", "../mcp_server"))
            exclude("**/__pycache__/**", "**/*.pyc")
        }
    }
}

intellijPlatform {
    pluginConfiguration {
        name = providers.gradleProperty("pluginName").get()
        version = project.version.toString()
    }
}

dependencies {
    intellijPlatform {
        val envRiderHome = providers.environmentVariable("RIDER_HOME").orNull
        val localRiderHome = if (!envRiderHome.isNullOrBlank() && file(envRiderHome).exists()) {
            envRiderHome
        } else {
            null
        }

        val fallbackVersion = providers.gradleProperty("riderPlatformVersion").orElse("2024.1").get()
        if (localRiderHome != null) {
            local(file(localRiderHome))
        } else {
            rider(fallbackVersion) {
                useInstaller.set(false)
            }
        }
        val bundled = providers.gradleProperty("platformPlugins").orNull
        if (!bundled.isNullOrBlank()) {
            bundledPlugin(bundled)
        }
    }
    implementation("io.github.icedland.iced:iced-x86:1.21.0")
}

java {
    toolchain {
        languageVersion.set(JavaLanguageVersion.of(21))
    }
}

tasks.withType<KotlinCompile>().configureEach {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_21)
        freeCompilerArgs.add("-Xjsr305=strict")
    }
}

tasks.named("buildSearchableOptions") {
    enabled = false
}

tasks.named("instrumentCode") {
    enabled = false
}

tasks.named("prepareJarSearchableOptions") {
    enabled = false
}
