using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TSCutter.GUI.Services.TransportStream.Analysis;

/// <summary>
/// 包装最终输出文件流，把每次成功写入的原始字节按顺序交给导出检测会话。
/// 同步与异步写入都覆盖；缓冲区在归还或复用前就完成检测，会话不持有调用方的 Memory/Span。
/// </summary>
internal sealed class TsDetectingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly ExportTsCheckSession _session;

    public TsDetectingWriteStream(Stream inner, ExportTsCheckSession session)
    {
        _inner = inner;
        _session = session;
    }

    public override bool CanRead => false;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var before = _inner.Position;
        var position = _inner.Seek(offset, origin);
        if (position != before)
            _session.MarkSeekDetected();
        return position;
    }

    public override void SetLength(long value)
    {
        var before = _inner.Length;
        _inner.SetLength(value);
        if (value != before)
            _session.MarkSeekDetected();
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        _session.Append(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        _session.Append(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _session.Append(buffer.Span);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        Dispose(false);
        GC.SuppressFinalize(this);
    }
}
