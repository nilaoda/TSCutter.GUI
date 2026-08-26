namespace TSCutter.GUI.Models;

/// <summary>
/// Describes how the most recently decoded frame reaches the preview.
/// Hardware decoding and GPU presentation are intentionally separate: a
/// hardware decoder can still use the CPU bitmap path when the UI backend
/// cannot import its native surface.
/// </summary>
public enum VideoPresentationMode
{
    SoftwareBitmap,
    HardwareCpuTransfer,
    HardwareGpu,
}
