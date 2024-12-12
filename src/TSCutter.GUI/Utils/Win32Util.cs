using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TSCutter.GUI.Utils;

[SupportedOSPlatform("windows")]
public static class Win32Util
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cItems, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr pbc, out IntPtr pidl, uint sfgaoIn, out uint psfgaoOut);

    public static void OpenFolderInExplorer(string filePath)
    {
        if (SHParseDisplayName(filePath, IntPtr.Zero, out var pidl, 0, out _) == 0)
        {
            SHOpenFolderAndSelectItems(pidl, 0, null!, 0);
            Marshal.FreeCoTaskMem(pidl);
        }
    }
}