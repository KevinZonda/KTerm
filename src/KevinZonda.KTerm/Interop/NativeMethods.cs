using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KevinZonda.KTerm.Interop;

internal static class NativeMethods
{
    internal const int WindowStyleIndex = -16;
    internal const nint WindowStylePopup = unchecked((int)0x8000_0000);
    internal const nint WindowStyleCaption = 0x00C0_0000;
    internal const nint WindowStyleSystemMenu = 0x0008_0000;
    internal const nint WindowStyleThickFrame = 0x0004_0000;
    internal const nint WindowStyleMinimizeBox = 0x0002_0000;
    internal const nint WindowStyleMaximizeBox = 0x0001_0000;
    internal const uint SetWindowPositionNoSize = 0x0001;
    internal const uint SetWindowPositionNoMove = 0x0002;
    internal const uint SetWindowPositionNoZOrder = 0x0004;
    internal const uint SetWindowPositionNoActivate = 0x0010;
    internal const uint SetWindowPositionFrameChanged = 0x0020;
    internal const int DwmUseImmersiveDarkMode = 20;
    internal const int DwmWindowCornerPreference = 33;
    internal const int DwmBorderColor = 34;
    internal const int DwmCaptionColor = 35;
    internal const int DwmTextColor = 36;
    internal const uint MonitorDefaultToNearest = 0x0000_0002;

    internal const uint ExtendedStartupInfoPresent = 0x0008_0000;
    internal const uint CreateUnicodeEnvironment = 0x0000_0400;
    internal const uint WaitObject0 = 0x0000_0000;
    internal const uint WaitTimeout = 0x0000_0102;
    internal const uint StillActive = 259;
    internal const nuint ProcThreadAttributePseudoConsole = 0x0002_0016;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Coord
    {
        internal short X;
        internal short Y;

        internal Coord(int columns, int rows)
        {
            X = checked((short)columns);
            Y = checked((short)rows);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        internal NativePoint Reserved;
        internal NativePoint MaxSize;
        internal NativePoint MaxPosition;
        internal NativePoint MinTrackSize;
        internal NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect WorkArea;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        internal int cb;
        internal string? lpReserved;
        internal string? lpDesktop;
        internal string? lpTitle;
        internal uint dwX;
        internal uint dwY;
        internal uint dwXSize;
        internal uint dwYSize;
        internal uint dwXCountChars;
        internal uint dwYCountChars;
        internal uint dwFillAttribute;
        internal uint dwFlags;
        internal ushort wShowWindow;
        internal ushort cbReserved2;
        internal IntPtr lpReserved2;
        internal IntPtr hStdInput;
        internal IntPtr hStdOutput;
        internal IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal IntPtr hProcess;
        internal IntPtr hThread;
        internal uint dwProcessId;
        internal uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes,
        uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref nuint lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        nuint attribute,
        IntPtr lpValue,
        nuint cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int CreatePseudoConsole(
        Coord size,
        IntPtr hInput,
        IntPtr hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [DllImport("kernel32.dll")]
    internal static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll")]
    internal static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int index, int newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(IntPtr hWnd, int index, nint newValue);

    internal static nint GetWindowLongPtr(IntPtr hWnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index) : GetWindowLong32(hWnd, index);

    internal static nint SetWindowLongPtr(IntPtr hWnd, int index, nint newValue) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, index, newValue)
            : SetWindowLong32(hWnd, index, newValue.ToInt32());

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageW(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(
        IntPtr hWnd,
        int attribute,
        ref int value,
        int valueSize);

    internal static Win32Exception LastError(string operation) =>
        new(Marshal.GetLastWin32Error(), operation);
}
