using System.Collections.Generic;

namespace TSCutter.GUI.Utils;

internal static class TsPidAllocator
{
    public const int FirstUserPid = 0x0100;
    public const int LastPid = 0x1FFE;

    public static bool TryTakeNext(HashSet<int> used, out int pid)
    {
        // 从非保留 PID 区间顺序分配，结果稳定且不会覆盖 PAT/PMT/PCR 等已登记 PID。
        for (var candidate = FirstUserPid; candidate <= LastPid; candidate++)
        {
            if (used.Add(candidate))
            {
                pid = candidate;
                return true;
            }
        }

        pid = -1;
        return false;
    }
}
