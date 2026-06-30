using System;

namespace ManagedInspectionEdgeCases
{
    public class HardIlCases
    {
        private static readonly string StaticPrefix = "edge";
        private readonly int _seed;

        public HardIlCases()
            : this(7)
        {
        }

        public HardIlCases(int seed)
        {
            _seed = seed;
        }

        public int DenseSwitch(int mode, int value)
        {
            switch (mode)
            {
                case 0:
                    return value + _seed;
                case 1:
                    return value - _seed;
                case 2:
                    return checked(value * 2);
                case 3:
                    return Helper(value);
                case 4:
                    return -value;
                case 5:
                    return value == 0 ? _seed : value / 2;
                default:
                    return value < 0 ? 0 : value;
            }
        }

        public string TokenHeavy<T>(T input, string suffix)
            where T : class
        {
            string text = input == null ? "<null>" : input.ToString();
            Type type = typeof(T);
            return string.Format("{0}:{1}:{2}:{3}", StaticPrefix, type.FullName, text, suffix);
        }

        public int Overloaded(int value)
        {
            return value + _seed;
        }

        public string Overloaded(string value)
        {
            return value == null ? StaticPrefix : StaticPrefix + ":" + value;
        }

        public int TryFinallyLoop(int count)
        {
            int total = 0;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    total += DenseSwitch(i % 6, i);
                }
            }
            finally
            {
                total += _seed;
            }

            return total;
        }

        private int Helper(int value)
        {
            return value ^ _seed;
        }
    }
}
