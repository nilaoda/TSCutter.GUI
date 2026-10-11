using System.IO;

namespace TSCutter.GUI.Utils;

internal static class MediaFileUtil
{
    /// <summary>
    /// 以只读方式打开媒体源文件，允许其他读取以及移动、重命名和删除。
    /// 仅在需要读取正在写入的文件时显式允许并发写入；调用方负责释放返回的流。
    /// </summary>
    public static FileStream OpenRead(string path, int bufferSize = 4096,
        FileOptions options = FileOptions.None, bool allowConcurrentWrite = false)
    {
        var share = FileShare.Read | FileShare.Delete;
        if (allowConcurrentWrite)
            share |= FileShare.Write;
        return new FileStream(path, FileMode.Open, FileAccess.Read, share, bufferSize, options);
    }
}
