using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Rendering.Gpu;

namespace TSCutter.GUI.Utils.Media;

/// <summary>
/// 可用于硬件解码的设备。Id 只由硬件属性组成，重启或枚举顺序变化后仍能找回同一设备；
/// DeviceType 与 DeviceArgument 直接交给 av_hwdevice_ctx_create，只在本次枚举内有效。
/// Luid 用于判断是否与渲染设备在同一张显卡上，平台没有该概念时为 0。
/// </summary>
internal sealed record HardwareDecodingDevice(string Id, string Name, AVHWDeviceType DeviceType,
    string DeviceArgument, long Luid = 0);

internal static class HardwareDecodingDevices
{
    /// <summary>
    /// 列出当前可选的硬件解码设备。Windows 按 DXGI 显卡列出（D3D11VA）；macOS 的 VideoToolbox 不接受设备参数、
    /// 由系统选择 GPU，内置的 Linux FFmpeg 不含硬件解码，这两个平台返回空列表，只能自动选择。
    /// </summary>
    internal static IReadOnlyList<HardwareDecodingDevice> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
            return [];
        try
        {
            return Describe(WindowsD3D11.EnumerateAdapters());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine($"Failed to enumerate graphics adapters: {exception.Message}");
            return [];
        }
    }

    /// <summary>生成持久化 Id；同型号的多张显卡按枚举顺序编号，名称也加上编号以便区分。</summary>
    internal static IReadOnlyList<HardwareDecodingDevice> Describe(IEnumerable<WindowsD3D11.AdapterInfo> adapters)
    {
        var devices = new List<HardwareDecodingDevice>();
        var counts = new Dictionary<string, int>();
        foreach (var adapter in adapters)
        {
            var key = $"d3d11va:{adapter.Vendor:x4}:{adapter.Device:x4}:{adapter.Subsystem:x8}:{adapter.Revision:x2}";
            var count = counts[key] = counts.GetValueOrDefault(key) + 1;
            var index = adapter.Index.ToString(CultureInfo.InvariantCulture);
            devices.Add(count == 1
                ? new(key, adapter.Name, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, index, adapter.Luid)
                : new($"{key}#{count}", $"{adapter.Name} #{count}", AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, index, adapter.Luid));
        }
        return devices;
    }

    /// <summary>按保存的 Id 查找设备；自动选择或设备已移除时返回 null，由调用方沿用自动选择。</summary>
    internal static HardwareDecodingDevice? Resolve(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        var device = Enumerate().FirstOrDefault(item => item.Id == id);
        if (device is null)
            Console.WriteLine($"Hardware decoding device {id} is unavailable; using automatic selection.");
        return device;
    }

    /// <summary>返回解码器实际使用的显卡，用于日志确认所选设备已生效；无法查询的平台返回 null。</summary>
    internal static unsafe string? DescribeAdapter(CodecContext codec)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        AVCodecContext* raw = codec;
        if (raw->hw_device_ctx == null)
            return null;
        var context = (AVHWDeviceContext*)raw->hw_device_ctx->data;
        if (context->type != AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)
            return null;
        var device = ((AVD3D11VADeviceContext*)context->hwctx)->device;
        try
        {
            return device == null ? null : WindowsD3D11.Adapter((nint)device, out _);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }
}
