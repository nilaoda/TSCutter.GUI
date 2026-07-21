using System;

namespace TSCutter.GUI.Services;

internal sealed class TsPsiSectionAssembler
{
    private readonly byte[] _buffer = new byte[4096];
    private int _length;
    private int _expectedLength;
    private bool _waitForPayloadStart;

    public void Push(ReadOnlySpan<byte> payload, bool payloadStart, Action<ReadOnlySpan<byte>> sectionHandler)
    {
        if (_waitForPayloadStart)
        {
            if (!payloadStart)
                return;
            _waitForPayloadStart = false;
        }
        // PSI section 可以跨多个 TS 包；pointer_field 之前的数据用于补完上一 section。
        if (payloadStart)
        {
            if (payload.IsEmpty)
                return;
            var pointer = payload[0];
            payload = payload[1..];
            if (pointer > payload.Length)
            {
                DiscardUntilPayloadStart();
                return;
            }

            if (_length > 0 && pointer > 0)
                Append(payload[..pointer], sectionHandler);
            payload = payload[pointer..];
            Reset();
        }

        Append(payload, sectionHandler);
    }

    private void Append(ReadOnlySpan<byte> data, Action<ReadOnlySpan<byte>> sectionHandler)
    {
        while (!data.IsEmpty)
        {
            if (_length == 0 && data[0] == 0xFF)
                return;

            var needed = _expectedLength > 0 ? _expectedLength - _length : Math.Min(3 - _length, data.Length);
            if (needed <= 0)
                needed = data.Length;
            var take = Math.Min(needed, data.Length);
            if (_length + take > _buffer.Length)
            {
                DiscardUntilPayloadStart();
                return;
            }

            data[..take].CopyTo(_buffer.AsSpan(_length));
            _length += take;
            data = data[take..];

            if (_length >= 3 && _expectedLength == 0)
            {
                _expectedLength = 3 + ((_buffer[1] & 0x0F) << 8) + _buffer[2];
                if (_expectedLength < 8 || _expectedLength > _buffer.Length)
                {
                    DiscardUntilPayloadStart();
                    return;
                }
            }

            if (_expectedLength > 0 && _length == _expectedLength)
            {
                sectionHandler(_buffer.AsSpan(0, _length));
                Reset();
            }
        }
    }

    private void Reset()
    {
        _length = 0;
        _expectedLength = 0;
    }

    public void Discard()
    {
        Reset();
        _waitForPayloadStart = false;
    }

    public void DiscardUntilPayloadStart()
    {
        Reset();
        _waitForPayloadStart = true;
    }
}
