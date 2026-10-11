#include <windows.h>
extern "C" __declspec(dllexport) __declspec(noinline) int performance_probe(int value) {
    volatile int total = value;
    for (int i = 0; i < 10000; ++i) total += i;
    return total;
}
__declspec(noinline) int deep_probe(int depth) {
    volatile int guard = depth;
    if (depth == 0) return performance_probe(7);
    return deep_probe(depth - 1) + guard;
}
int main() { return deep_probe(32); }
