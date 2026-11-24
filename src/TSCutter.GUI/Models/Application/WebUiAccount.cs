using System;

namespace TSCutter.GUI.Models.Application;

public sealed record WebUiAccount
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string UserName { get; init; } = string.Empty;
    public string PasswordHash { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    // 目录为空时继承宿主默认值，权限不接受工作进程回传的偏好覆盖。
    public string? InputDirectory
    {
        get; init;
    }
    public string? OutputDirectory
    {
        get; init;
    }
    public string? Preferences
    {
        get; init;
    }
}
