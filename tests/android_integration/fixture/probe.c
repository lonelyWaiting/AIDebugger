#include <jni.h>

volatile int watched_value;

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *reserved) {
    return JNI_VERSION_1_6;
}

__attribute__((noinline)) static int add_seven(int seed) {
    return seed + 7;
}

JNIEXPORT jint JNICALL
Java_com_aidebugger_dualprobe_ProbeActivity_nativeTick(JNIEnv *env, jclass klass, jint seed) {
    int answer = add_seven(seed);
    watched_value = answer;
    return answer;
}
