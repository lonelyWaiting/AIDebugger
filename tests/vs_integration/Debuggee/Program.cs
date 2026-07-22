using System;
using System.Threading;

namespace AIDebugger.LiveSmoke.Debuggee
{
    internal static class Program
    {
        private static void Main()
        {
            int seed = 41;
            int answer = Compute(seed); // BREAKPOINT_MARKER
            Console.WriteLine(answer);
            Thread.Sleep(TimeSpan.FromSeconds(30));
        }

        private static int Compute(int value)
        {
            return value + 1;
        }
    }
}
