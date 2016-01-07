using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Faolan.Core
{
    public static class ConsoleTools
    {
        public delegate bool ConsoleEventDelegate(CtrlType eventType);

        public enum CtrlType
        {
            CtrlCEvent = 0,
            CtrlBreakEvent = 1,
            CtrlCloseEvent = 2,
            CtrlLogoffEvent = 5,
            CtrlShutdownEvent = 6
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
            var handle = GetStdHandle(StdInputHandle);
            GetConsoleMode(handle, out var mode);
            SetConsoleMode(handle, mode | ENABLE_QUICK_EDIT_MODE);
        }

        // https://stackoverflow.com/questions/4646827/on-exit-for-a-console-application
        [DllImport("kernel32.dll")]
        public static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);

        // http://blog.rastating.com/setting-default-currentculture-in-all-versions-of-net/
        public static void SetDefaultCulture(CultureInfo culture)
        {
            /*var type = typeof(CultureInfo);

            try
            {
                type.InvokeMember("s_userDefaultCulture",
                    BindingFlags.SetField | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    culture,
                    new object[] {culture});

                type.InvokeMember("s_userDefaultUICulture",
                    BindingFlags.SetField | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    culture,
                    new object[] {culture});
            }
            catch
            {
            }

            try
            {
                type.InvokeMember("m_userDefaultCulture",
                    BindingFlags.SetField | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    culture,
                    new object[] {culture});

                type.InvokeMember("m_userDefaultUICulture",
                    BindingFlags.SetField | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    culture,
                    new object[] {culture});
            }
            catch
            {
            }*/
        }
    }
}