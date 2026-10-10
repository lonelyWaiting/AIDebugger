#include <windows.h>
extern "C" __declspec(dllexport) int probe(int value) { return value + 7; }
int main() { for (int i = 0; i < 200; ++i) Sleep(50); return probe(0); }
