"""Build the Java/JNI emulator smoke fixture on Windows."""
import argparse
from pathlib import Path
import subprocess
import zipfile


def build(sdk, jdk, output, build_tools, ndk):
    fixture = Path(__file__).resolve().with_name("fixture")
    tools = sdk / "build-tools" / build_tools
    android = sdk / "platforms/android-35/android.jar"
    llvm = sdk / "ndk" / ndk / "toolchains/llvm/prebuilt/windows-x86_64/bin"
    java = jdk / "bin/java.exe"
    classes, dex = output / "classes", output / "dex"
    library = output / "lib/x86_64/libdualprobe.so"
    for directory in (classes, dex, library.parent):
        directory.mkdir(parents=True, exist_ok=True)

    def run(*args):
        subprocess.run(args, check=True)

    run(jdk / "bin/javac.exe", "--release", "8", "-g", "-classpath", android,
        "-d", classes, fixture / "ProbeActivity.java")
    run(java, "-cp", tools / "lib/d8.jar", "com.android.tools.r8.D8", "--debug",
        "--min-api", "23", "--lib", android, "--output", dex,
        *sorted(classes.rglob("*.class")))
    run(llvm / "clang.exe", "--target=x86_64-linux-android24", "-g", "-O0", "-fPIC", "-shared",
        fixture / "probe.c", "-o", library)
    unsigned = output / "dual-unsigned.apk"
    run(tools / "aapt2.exe", "link", "-I", android, "--manifest", fixture / "AndroidManifest.xml",
        "-o", unsigned)
    with zipfile.ZipFile(unsigned, "a") as apk:
        apk.write(dex / "classes.dex", "classes.dex")
        apk.write(library, "lib/x86_64/libdualprobe.so")
    keystore = output / "fixture.jks"
    if not keystore.exists():
        run(jdk / "bin/keytool.exe", "-genkeypair", "-keystore", keystore, "-storepass", "android",
            "-keypass", "android", "-alias", "probe", "-dname", "CN=AIDebugger Fixture",
            "-keyalg", "RSA", "-validity", "3650")
    run(java, "-jar", tools / "lib/apksigner.jar", "sign", "--ks", keystore, "--ks-pass", "pass:android",
        "--out", output / "dual.apk", unsigned)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk", type=Path, required=True)
    parser.add_argument("--jdk", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--build-tools", default="36.1.0")
    parser.add_argument("--ndk", default="27.2.12479018")
    args = parser.parse_args()
    build(args.sdk.resolve(), args.jdk.resolve(), args.output.resolve(), args.build_tools, args.ndk)
