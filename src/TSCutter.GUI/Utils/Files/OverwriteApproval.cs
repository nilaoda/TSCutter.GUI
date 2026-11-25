using System.Threading;

namespace TSCutter.GUI.Utils.Files;

// 队列项持有按路径的一次性许可，写入前未使用的许可归还以供重试。
internal sealed class OverwriteApproval(string path)
{
    private readonly string target = MediaFileUtil.CanonicalPath(path);
    private int available = 1;

    internal bool Allows(string path) => Volatile.Read(ref available) != 0 &&
        MediaFileUtil.PathComparer.Equals(target, MediaFileUtil.CanonicalPath(path));

    internal void Restore() => Volatile.Write(ref available, 1);

    internal bool TryConsume(string path) => Allows(path) && Interlocked.Exchange(ref available, 0) != 0;
}
