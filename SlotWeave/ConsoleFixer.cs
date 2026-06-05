using System.Runtime.InteropServices;

namespace SlotWeave;

internal class ConsoleFixer {
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int pid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_QUICK_EDIT = 0x0040;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;

    public static void Init() {
        if (Environment.GetEnvironmentVariable("GDWEAVE_CONSOLE") is not null) {
            AllocConsole();
            AttachConsole(-1);

            // Disable Quick Edit mode — clicking console freezes the game thread
            try {
                var handle = GetStdHandle(STD_OUTPUT_HANDLE);
                if (handle != IntPtr.Zero && handle != new IntPtr(-1)) {
                    if (GetConsoleMode(handle, out var mode)) {
                        mode &= ~ENABLE_QUICK_EDIT;
                        mode |= ENABLE_EXTENDED_FLAGS;
                        SetConsoleMode(handle, mode);
                    }
                }
            } catch { /* best effort */ }
        }

        var stdout = new StreamWriter(Console.OpenStandardOutput()) {AutoFlush = true};
        Console.SetOut(stdout);
        var stderr = new StreamWriter(Console.OpenStandardError()) {AutoFlush = true};
        Console.SetError(stderr);
    }
}
