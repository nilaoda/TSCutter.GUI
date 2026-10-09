using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace TSCutter.GUI.Utils;

internal static class TsBatchCheckReportWriter
{
    public static async Task<string> SaveLogAsync(string sourcePath, string? outputFolder, string text)
    {
        var folder = outputFolder ?? Path.GetDirectoryName(sourcePath)!;
        var name = Path.GetFileName(sourcePath) + ".check";
        // CreateNew 同时保护已有报告和原始文件，多个批量窗口也不会互相覆盖。
        for (var index = 0; ; index++)
        {
            var path = Path.Combine(folder, name + (index == 0 ? "" : $".{index}") + ".log");
            FileStream stream;
            try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true); }
            catch (IOException) when (File.Exists(path)) { continue; }
            try
            {
                await using (stream)
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                    await writer.WriteAsync(text);
                return path;
            }
            catch
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    public static string CsvCell(string value)
    {
        // 避免路径或错误消息被电子表格解释为公式。
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
