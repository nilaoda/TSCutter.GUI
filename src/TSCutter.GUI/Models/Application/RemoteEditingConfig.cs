using System.Collections.Generic;

namespace TSCutter.GUI.Models.Application;

// 远程宿主设置独立落盘，普通桌面配置和编辑工作进程都不负责保存它。
public sealed class RemoteEditingConfig
{
    public string? InputDirectory
    {
        get; set;
    }
    public string? OutputDirectory
    {
        get; set;
    }
    public int Port { get; set; } = 5360;
    public bool AllowLan
    {
        get; set;
    } = true;
    public bool EnableIpv6
    {
        get; set;
    }
    public int MaxWorkers { get; set; } = 4;
    public List<WebUiAccount> Accounts { get; set; } = [];

    internal static RemoteEditingConfig From(AppConfig config) => new()
    {
        InputDirectory = config.WebUiInputDirectory,
        OutputDirectory = config.WebUiOutputDirectory,
        Port = config.WebUiPort,
        AllowLan = config.WebUiAllowLan,
        EnableIpv6 = config.WebUiEnableIpv6,
        MaxWorkers = config.WebUiMaxWorkers,
        Accounts = config.WebUiAccounts
    };

    internal void ApplyTo(AppConfig config)
    {
        config.WebUiInputDirectory = InputDirectory;
        config.WebUiOutputDirectory = OutputDirectory;
        config.WebUiPort = Port;
        config.WebUiAllowLan = AllowLan;
        config.WebUiEnableIpv6 = EnableIpv6;
        config.WebUiMaxWorkers = MaxWorkers;
        config.WebUiAccounts = Accounts ?? [];
    }
}
