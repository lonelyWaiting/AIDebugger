#include <windows.h>

#include <cstdio>

__declspec(noinline) int OptimizedProbe(int value)
{
    int foldedConstant = 1234;
    int computed = value * 7;
    if (computed == -1)
    {
        return foldedConstant;
    }
    return computed + 3;
}

int main()
{
    std::printf("OptimizedBreakpointProbe pid=%lu\n", GetCurrentProcessId());
    std::fflush(stdout);
    for (int i = 0; i < 1200; ++i)
    {
        int value = OptimizedProbe(i);
        if (value == 424242)
        {
            std::printf("impossible: %d\n", value);
        }
        Sleep(100);
    }
    return 0;
}
