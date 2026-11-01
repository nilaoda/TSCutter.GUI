using System;
using System.Runtime.InteropServices;

namespace TSCutter.GUI.Rendering.Gpu;

// 复用 Avalonia 自带 ANGLE 的 EGL_* 导出，兼容动态库和静态链接。
internal static unsafe partial class WindowsAngle
{
    private const string Library = "av_libglesv2";
    internal const uint Texture2D = 0xde1;
    internal const int None = 0x3038;
    [LibraryImport(Library, EntryPoint = "EGL_GetCurrentDisplay")] internal static partial nint Display();
    [LibraryImport(Library, EntryPoint = "EGL_GetCurrentContext")] internal static partial nint Context();
    [LibraryImport(Library, EntryPoint = "EGL_QueryString")] internal static partial nint QueryString(nint display, int name);
    [LibraryImport(Library, EntryPoint = "EGL_GetError")] internal static partial int Error();
    [LibraryImport(Library, EntryPoint = "EGL_QueryDisplayAttribEXT")] internal static partial int QueryDisplay(nint display, int attribute, out nint value);
    [LibraryImport(Library, EntryPoint = "EGL_QueryDeviceAttribEXT")] internal static partial int QueryDevice(nint device, int attribute, out nint value);
    [LibraryImport(Library, EntryPoint = "EGL_CreateImageKHR")] internal static partial nint CreateImage(nint display, nint context, int target, nint buffer, int* attributes);
    [LibraryImport(Library, EntryPoint = "EGL_DestroyImageKHR")] internal static partial int DestroyImage(nint display, nint image);
    [LibraryImport(Library)] internal static partial nint glGetString(uint name);
    [LibraryImport(Library)] internal static partial uint glGetError();
    [LibraryImport(Library)] internal static partial void glGenTextures(int count, out uint texture);
    [LibraryImport(Library)] internal static partial void glDeleteTextures(int count, ref uint texture);
    [LibraryImport(Library)] internal static partial void glBindTexture(uint target, uint texture);
    [LibraryImport(Library)] internal static partial void glGetIntegerv(uint name, out int value);
    [LibraryImport(Library)] internal static partial void glTexParameteri(uint target, uint name, int value);
    [LibraryImport(Library)] internal static partial void glEGLImageTargetTexture2DOES(uint target, nint image);
    [LibraryImport(Library)] internal static partial void glFlush();
    internal static string Text(nint pointer) => Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
    internal static bool Has(string extensions, string name) => (" " + extensions + " ").Contains(" " + name + " ", StringComparison.Ordinal);
    internal static void CheckGl()
    {
        var error = glGetError();
        if (error != 0)
            throw new NotSupportedException($"ANGLE texture import GL error 0x{error:x}.");
    }
}
