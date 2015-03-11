using System;
using System.Runtime.InteropServices;

namespace LibFaolan.DllImport
{
    public static class Other
    {
        [DllImport("msvcrt.dll", EntryPoint = "_time32", CallingConvention = CallingConvention.Cdecl)]
        public static extern int time(ref int timer);
    }

    public static class Network
    {
        [DllImport("Ws2_32.dll", CharSet = CharSet.Ansi)]
        public static extern uint inet_addr(string cp);

        [DllImport("Ws2_32.dll")]
        public static extern uint htonl(uint hostlong);
    }

    public static class Console
    {
        public delegate bool ConsoleEventDelegate(CtrlType eventType);

        public enum CtrlType
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1,
            CTRL_CLOSE_EVENT = 2,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT = 6
        }

        private const int StdInputHandle = -10;
        // ENABLE_QUICK_EDIT_MODE ENABLE_EXTENDED_FLAGS
        private const int ENABLE_QUICK_EDIT_MODE = 0x40 | 0x80;

        [DllImport("kernel32.dll")]
        public static extern bool SetConsoleMode(IntPtr hConsoleHandle, int mode);

        [DllImport("kernel32.dll")]
        public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out int mode);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(int handle);

        // https://stackoverflow.com/questions/6828450/console-application-with-selectable-text
        public static void EnableQuickEditMode()
        {
            int mode;
            var handle = GetStdHandle(StdInputHandle);
            GetConsoleMode(handle, out mode);
            SetConsoleMode(handle, mode | ENABLE_QUICK_EDIT_MODE);
        }

        // https://stackoverflow.com/questions/4646827/on-exit-for-a-console-application
        [DllImport("kernel32.dll")]
        public static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);
    }
}