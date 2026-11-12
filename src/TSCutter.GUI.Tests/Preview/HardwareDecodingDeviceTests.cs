using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.ViewModels.Application;
using Xunit;

namespace TSCutter.GUI.Tests.Preview;

public class HardwareDecodingDeviceTests
{
    [Fact]
    public void IdenticalCardsGetDistinctIdsAndKeepTheirEnumerationIndex()
    {
        var devices = HardwareDecodingDevices.Describe([
            new(0, "Card A", 0x1234, 0x5678, 0x9abcdef0, 0x01, 11),
            new(2, "Card B", 0x4321, 0x8765, 0x0fedcba9, 0x02, 22),
            new(3, "Card A", 0x1234, 0x5678, 0x9abcdef0, 0x01, 33)
        ]);

        Assert.Equal(["d3d11va:1234:5678:9abcdef0:01", "d3d11va:4321:8765:0fedcba9:02", "d3d11va:1234:5678:9abcdef0:01#2"],
            devices.Select(device => device.Id));
        Assert.Equal(["Card A", "Card B", "Card A #2"], devices.Select(device => device.Name));
        // 设备参数是包含被跳过项在内的原始枚举序号，FFmpeg 按它选择显卡。
        Assert.Equal(["0", "2", "3"], devices.Select(device => device.DeviceArgument));
        Assert.All(devices, device => Assert.Equal(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, device.DeviceType));
    }

    [Fact]
    public void AutomaticOrRemovedDeviceResolvesToAutomaticButSettingsKeepTheSavedChoice()
    {
        Assert.Null(HardwareDecodingDevices.Resolve(null));
        Assert.Null(HardwareDecodingDevices.Resolve("d3d11va:ffff:ffff:00000000:00"));

        HardwareDecodingDevice[] present = [new("d3d11va:a", "Card A", AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, "0")];
        var options = SettingsWindowViewModel.CreateHardwareDecodingDeviceOptions("d3d11va:removed", present);
        Assert.Equal([null, "d3d11va:a", "d3d11va:removed"], options.Select(option => option.Id));
        Assert.True(options[0].IsAutomatic);
        Assert.True(options[2].IsMissing);
        Assert.Equal(2, SettingsWindowViewModel.CreateHardwareDecodingDeviceOptions("d3d11va:a", present).Count);
    }
}
