using System;
using System.Runtime.InteropServices;

namespace TSCutter.GUI.Rendering.Gpu;

// 通过原生 COM 虚表调用，兼容 AOT；返回的接口引用由调用方释放。
internal static unsafe class WindowsD3D11
{
    internal const uint ShaderResource = 8, Decoder = 0x200;
    [StructLayout(LayoutKind.Sequential)]
    internal struct TextureDescription
    {
        internal uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality;
        internal uint Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    private static void** V(nint value) => *(void***)value;
    internal static void AddRef(nint value) => ((delegate* unmanaged[Stdcall]<nint, uint>)V(value)[1])(value);
    internal static void Release(nint value)
    {
        if (value != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)V(value)[2])(value);
    }
    internal static nint Query(nint value, Guid iid)
    {
        nint result = 0;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)V(value)[0])(value, &iid, &result));
        return result;
    }
    internal static nint GetDevice(nint texture)
    {
        nint result = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(texture)[3])(texture, &result);
        return result;
    }
    internal static TextureDescription Describe(nint texture)
    {
        TextureDescription description;
        ((delegate* unmanaged[Stdcall]<nint, TextureDescription*, void>)V(texture)[10])(texture, &description);
        return description;
    }
    internal static nint CreateTexture(nint device, TextureDescription description)
    {
        nint result = 0;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, TextureDescription*, void*, nint*, int>)V(device)[5])(
            device, &description, null, &result));
        return result;
    }
    internal static nint ImmediateContext(nint device)
    {
        nint result = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)V(device)[40])(device, &result);
        return result;
    }
    internal static void CheckDevice(nint device) => Marshal.ThrowExceptionForHR(
        ((delegate* unmanaged[Stdcall]<nint, int>)V(device)[39])(device));
    internal static void Copy(nint context, nint destination, nint source, uint subresource) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, void*, void>)V(context)[46])(
            context, destination, 0, 0, 0, 0, source, subresource, null);

    internal static nint CreateCompletionQuery(nint device)
    {
        uint* description = stackalloc uint[] { 0, 0 }; // 事件查询（D3D11_QUERY_EVENT），MiscFlags 为 0。
        nint query = 0;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)V(device)[24])(device, description, &query));
        return query;
    }
    internal static void Signal(nint context, nint query)
    {
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)V(context)[28])(context, query); // 结束查询（End）。
        ((delegate* unmanaged[Stdcall]<nint, void>)V(context)[111])(context); // 提交命令，不等待 GPU。
    }
    internal static bool IsComplete(nint context, nint query)
    {
        int completed = 0;
        var result = ((delegate* unmanaged[Stdcall]<nint, nint, void*, uint, uint, int>)V(context)[29])(
            context, query, &completed, sizeof(int), 1); // 只读取状态，不触发提交（D3D11_ASYNC_GETDATA_DONOTFLUSH）。
        Marshal.ThrowExceptionForHR(result);
        return result == 0 && completed != 0;
    }

    internal static void ProtectContext(nint device)
    {
        var context = ImmediateContext(device);
        nint multithread = 0;
        try
        {
            multithread = Query(context, new Guid("9b7e4e00-342c-4106-a19f-4f2704f689f0"));
            ((delegate* unmanaged[Stdcall]<nint, int, int>)V(multithread)[5])(multithread, 1);
        }
        finally
        {
            Release(multithread);
            Release(context);
        }
    }

    // 固定宽字符数组和 SIZE_T 字段必须保持 DXGI_ADAPTER_DESC 的原生布局。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        internal fixed char Description[128];
        internal uint Vendor, Device, Subsystem, Revision;
        internal nuint DedicatedVideo, DedicatedSystem, SharedSystem;
        internal uint LuidLow;
        internal int LuidHigh;
    }
    internal static string Adapter(nint device)
    {
        var dxgi = Query(device, new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"));
        nint adapter = 0;
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)V(dxgi)[7])(dxgi, &adapter));
            AdapterDescription description;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)V(adapter)[8])(adapter, &description));
            return $"{new string(description.Description)}; LUID {description.LuidHigh:x8}:{description.LuidLow:x8}";
        }
        finally
        {
            Release(adapter);
            Release(dxgi);
        }
    }
}
