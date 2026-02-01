import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import org.jetbrains.kotlin.gradle.tasks.KotlinCompile

plugins {
    kotlin("jvm") version "2.1.0"
    id("org.jetbrains.intellij.platform") version "2.11.0"
}

group = providers.gradleProperty("pluginGroup").get()
version = providers.gradleProperty("pluginVersion").get()

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
        version = providers.gradleProperty("pluginVersion").get()
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
