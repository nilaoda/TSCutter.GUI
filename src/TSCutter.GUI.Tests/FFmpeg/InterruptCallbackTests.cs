using System.Runtime.InteropServices;
using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.FFmpeg;

public sealed class InterruptCallbackTests
{
    [Fact]
    public unsafe void NativeCallbackReturnsInterruptWhenPredicateThrows()
    {
        foreach (var mode in new[] { 0, 1, 2 })
        {
            var callback = InterruptibleInputFormatContext.CreateInterruptCallback(() => mode switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidOperationException("Cancellation source failed.")
            });
            var pointer = (delegate* unmanaged[Cdecl]<void*, int>)(void*)Marshal.GetFunctionPointerForDelegate(callback);
            Assert.Equal(mode == 0 ? 0 : 1, pointer(null));
            GC.KeepAlive(callback);
        }
    }
}
