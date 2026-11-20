package com.aidebugger.dualprobe;

import android.app.Activity;
import android.os.Bundle;

public final class ProbeActivity extends Activity {
    private static volatile int sink;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        new Thread(() -> {
            entry(35);
            inspectObjects(35);
            while (!Thread.currentThread().isInterrupted()) {
                sink = javaTick(35);
                try {
                    Thread.sleep(50);
                } catch (InterruptedException stop) {
                    return;
                }
            }
        }, "aidb-probe").start();
    }

    private static void entry(int seed) {
        System.loadLibrary("dualprobe");
        sink = javaTick(seed);
    }

    private static int javaTick(int seed) {
        int pending = seed + 1;
        int value = nativeTick(pending);
        return afterNative(value);
    }

    private static int afterNative(int value) {
        return value * 2;
    }

    private static final class Payload {
        final int number;
        final String label;

        Payload(int number, String label) {
            this.number = number;
            this.label = label;
        }
    }

    private static void inspectObjects(int seed) {
        Payload payload = new Payload(seed, "android-probe");
        int[] data = new int[] {seed, seed + 1};
        sink = payload.number + data[1]; // JDWP object inspection stop
        try {
            throw new IllegalStateException(payload.label);
        } catch (IllegalStateException expected) {
            sink += expected.getMessage().length();
        }
    }

    private static native int nativeTick(int seed);
}
