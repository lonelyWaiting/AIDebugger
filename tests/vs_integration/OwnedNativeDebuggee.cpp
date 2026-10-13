#include <windows.h>
#include <cstdio>
#include <cstring>

unsigned char g_memory[2 * 1024 * 1024 + 64];
volatile int g_sink = 0;

__declspec(noinline) int OwnedProbe(int seed)
{
    volatile int result = seed + 7; // VS_LIVE_BREAKPOINT
    g_sink = result + 3;
    return g_sink;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 2) return 2;
    std::memset(g_memory, 0xCC, sizeof(g_memory));
    const char pattern[] = "AIDB-LIVE-BOUNDARY-20261013";
    std::memcpy(g_memory + 1024 * 1024 - 5, pattern, sizeof(pattern) - 1);
    FILE* ready = nullptr;
    if (_wfopen_s(&ready, argv[1], L"w") != 0 || !ready) return 3;
    std::fprintf(ready,
        "{\"processId\":%lu,\"address\":\"0x%llX\",\"size\":%zu,"
        "\"matchAddress\":\"0x%llX\",\"pattern\":\"%s\"}",
        GetCurrentProcessId(), reinterpret_cast<unsigned long long>(g_memory), sizeof(g_memory),
        reinterpret_cast<unsigned long long>(g_memory + 1024 * 1024 - 5), pattern);
    std::fclose(ready);
    for (int index = 0; index < 6000; ++index)
    {
        OwnedProbe(index);
        Sleep(100);
    }
    return 0;
}
