using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace TSCutter.GUI.Models.Media;

/// <summary>一条转码输出：从哪个声道组、用什么编码和声道。</summary>
public sealed class DolbyEOutput : AudioOutput
{
    /// <summary>声道组序号，从 1 开始。</summary>
    public int Program { get; set; } = 1;

    public DolbyEOutput Snapshot() => CopyTo(new DolbyEOutput { Program = Program });
}

/// <summary>一条杜比 E 轨（按 PID）的导出处理方式。</summary>
public sealed class DolbyETrackSettings
{
    public int Pid { get; set; }
    /// <summary>设置时读到的声道组配置（决定各声道组的声道布局）；-1 表示未知。</summary>
    public int ProgramConfig { get; set; } = -1;
    /// <summary>false 表示不处理，原样保留杜比 E 轨。</summary>
    public bool Transcode { get; set; }
    /// <summary>转码时是否同时保留原始杜比 E 轨；不保留时转码结果替换原轨。</summary>
    public bool KeepOriginal { get; set; }
    public List<DolbyEOutput> Outputs { get; set; } = [];

    [JsonIgnore]
    public bool HasWork => Transcode && Outputs.Any(output => output.Enabled);

    public DolbyETrackSettings Snapshot() => new()
    {
        Pid = Pid, ProgramConfig = ProgramConfig, Transcode = Transcode, KeepOriginal = KeepOriginal,
        Outputs = Outputs.Select(output => output.Snapshot()).ToList()
    };
}
