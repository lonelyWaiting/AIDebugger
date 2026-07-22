import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import org.jetbrains.kotlin.gradle.tasks.KotlinCompile
import java.util.concurrent.TimeUnit

plugins {
    kotlin("jvm") version "2.1.0"
    id("org.jetbrains.intellij.platform") version "2.11.0"
}

// ---------------------------------------------------------------------------
// Git-based version: exact vX.Y.Z tag -> X.Y.Z; otherwise X.Y.Z.<distance>.
// Falls back to gradle.properties pluginVersion when git is unavailable.
// ---------------------------------------------------------------------------
fun gitVersion(): String {
    fun exec(vararg cmd: String): String? = try {
        val p = ProcessBuilder(*cmd).redirectErrorStream(true).start()
        val out = p.inputStream.bufferedReader().readText().trim()
        if (p.waitFor(5, TimeUnit.SECONDS) && p.exitValue() == 0) out else null
    } catch (_: Exception) { null }

    exec("git", "describe", "--tags", "--long", "--match", "v[0-9]*.[0-9]*.[0-9]*", "HEAD")?.let { description ->
        Regex("^v?(\\d+)\\.(\\d+)\\.(\\d+)-(\\d+)-g[0-9a-f]+$").matchEntire(description)?.let { match ->
            val baseVersion = match.groupValues.subList(1, 4).joinToString(".")
            val distance = match.groupValues[4].toInt()
            return if (distance == 0) baseVersion else "$baseVersion.$distance"
        }
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
