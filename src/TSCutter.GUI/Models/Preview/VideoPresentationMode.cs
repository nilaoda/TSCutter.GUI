namespace TSCutter.GUI.Models.Preview;

/// <summary>
/// 当前帧的预览呈现方式。硬件解码与 GPU 呈现分别记录；
/// 界面无法导入硬件表面时，硬解画面仍可回退到 CPU 位图。
/// </summary>
public enum VideoPresentationMode
{
    SoftwareBitmap,
    HardwareCpuTransfer,
    HardwareGpu,
    SoftwareGpu,
    HardwareGpuUpload,
    HardwareGpuCopy,
}
