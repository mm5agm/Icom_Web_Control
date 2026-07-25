using System;
using System.Collections.Generic;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// Byte-stream framer for CI-V. Serial bytes arrive in arbitrary chunks;
    /// this accumulates them and raises <see cref="FrameReceived"/> once per
    /// complete <c>FE FE … FD</c> frame. Leading noise before a preamble and
    /// partial trailing frames are handled so no valid frame is ever split or
    /// lost across reads.
    ///
    /// This is the Icom replacement for the Yaesu <c>CatMessageBuffer</c>
    /// (which framed on ASCII <c>;</c>). Echo suppression is NOT done here — a
    /// frame is a frame regardless of direction; <see cref="CivBusService"/>
    /// discards our own bus echo by inspecting the address bytes.
    /// </summary>
    public sealed class CivFrameBuffer
    {
        private readonly List<byte> _buffer = new();

        public event EventHandler<CivFrame>? FrameReceived;

        public void Append(ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
                _buffer.Add(b);

            ExtractFrames();
        }

        public void Clear() => _buffer.Clear();

        private void ExtractFrames()
        {
            while (true)
            {
                // Locate the FE FE preamble.
                int start = -1;
                for (int i = 0; i + 1 < _buffer.Count; i++)
                {
                    if (_buffer[i] == CivProtocol.Preamble && _buffer[i + 1] == CivProtocol.Preamble)
                    {
                        start = i;
                        break;
                    }
                }

                if (start < 0)
                {
                    // No preamble yet. Drop everything except a possible lone
                    // trailing 0xFE that could be the first half of the next
                    // preamble.
                    if (_buffer.Count > 0 && _buffer[^1] == CivProtocol.Preamble)
                    {
                        if (_buffer.Count > 1)
                            _buffer.RemoveRange(0, _buffer.Count - 1);
                    }
                    else
                    {
                        _buffer.Clear();
                    }
                    return;
                }

                // Discard any noise before the preamble.
                if (start > 0)
                    _buffer.RemoveRange(0, start);

                // Find the FD terminator after the two preamble bytes. (Some
                // radios emit three or more FEs; searching from index 2 skips
                // the guaranteed pair and any extras are harmless leading body
                // bytes that Parse rejects.)
                int end = -1;
                for (int i = 2; i < _buffer.Count; i++)
                {
                    if (_buffer[i] == CivProtocol.End)
                    {
                        end = i;
                        break;
                    }
                }

                if (end < 0)
                    return; // Frame not complete yet — wait for more bytes.

                var frameBytes = _buffer.GetRange(0, end + 1).ToArray();
                _buffer.RemoveRange(0, end + 1);

                var frame = Parse(frameBytes);
                if (frame != null)
                    FrameReceived?.Invoke(this, frame);
            }
        }

        private static CivFrame? Parse(byte[] f)
        {
            // Shortest valid frame is FE FE <to> <from> FD (no body).
            if (f.Length < 5 || f[0] != CivProtocol.Preamble || f[1] != CivProtocol.Preamble || f[^1] != CivProtocol.End)
                return null;

            return new CivFrame
            {
                To = f[2],
                From = f[3],
                Body = f[4..^1],
            };
        }
    }
}
