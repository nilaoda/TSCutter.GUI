using System.Runtime.InteropServices;
using Avalonia.Headless;
using TSCutter.GUI.Utils.Application;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class MacOSWheelEventTests
{
    [MacOSWheelFact]
    public async Task GeneratedNativeEventsDistinguishPreciseScrollingAndRejectMismatchedInput()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
            var pool = Send(GetClass("NSAutoreleasePool"), Selector("new"));
            var ev = CreateScrollEvent(IntPtr.Zero, 0, 1, -1, 0, 0);
            Assert.NotEqual(IntPtr.Zero, ev);
            try
            {
                IntPtr Read(bool precise)
                {
                    SetField(ev, 88, precise ? 1 : 0);
                    var nativeEvent = SendObject(GetClass("NSEvent"), Selector("eventWithCGEvent:"), ev);
                    Assert.NotEqual(IntPtr.Zero, nativeEvent);
                    return nativeEvent;
                }
                var precise = Read(true);
                var timestamp = (ulong)(GetDouble(precise, Selector("timestamp")) * 1000);
                Assert.True(MacOSWheelInput.IsPreciseEvent(precise, IntPtr.Zero, timestamp));
                Assert.False(MacOSWheelInput.IsPreciseEvent(precise, IntPtr.Zero, timestamp + 1));
                Assert.False(MacOSWheelInput.IsPreciseEvent(precise, (IntPtr)1, timestamp));
                var discrete = Read(false);
                Assert.False(MacOSWheelInput.IsPreciseEvent(discrete, IntPtr.Zero, timestamp));
                Assert.False(MacOSWheelInput.IsPreciseEvent(IntPtr.Zero, IntPtr.Zero, timestamp));
            }
            finally
            {
                Release(ev);
                Send(pool, Selector("drain"));
            }
        }, CancellationToken.None);
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", EntryPoint = "CGEventCreateScrollWheelEvent2")]
    private static extern IntPtr CreateScrollEvent(IntPtr source, uint units, uint count, int first, int second, int third);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", EntryPoint = "CGEventSetIntegerValueField")]
    private static extern void SetField(IntPtr ev, uint field, long value);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
    private static extern void Release(IntPtr value);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector(string name);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern double GetDouble(IntPtr target, IntPtr selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendObject(IntPtr target, IntPtr selector, IntPtr value);
}

public sealed class MacOSWheelFactAttribute : FactAttribute
{
    public MacOSWheelFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "Requires macOS AppKit.";
    }
}
