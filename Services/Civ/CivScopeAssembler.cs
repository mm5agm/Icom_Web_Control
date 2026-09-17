using System;
using System.Collections.Generic;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// A completed spectrum-scope sweep: the full trace as dBFS-like bins across
    /// the span, plus the centre frequency and total span width the radio
    /// reported for the frame.
    ///
    /// <see cref="CentreHz"/> is not decoration - the SpectrumPanel builds its
    /// whole frequency axis from it, and it is not the VFO. Even in Center mode
    /// the IC-7300 centres on the passband rather than on the suppressed
    /// carrier, a fixed 1.5 kHz off (measured 2026-08-30):
    ///
    ///   40m LSB     VFO 7,174,000   sweep centre 7,172,500   IF width 3600
    ///   20m DATA-U  VFO 14,190,000  sweep centre 14,191,500  IF width 500
    ///
    /// Below the carrier in LSB, above it in USB, and the same 1500 Hz at both
    /// filter widths - so it is the sideband's carrier point, not half the
    /// filter. Centring the axis on the VFO instead would mislabel every bin by
    /// that much, about 3.6 bins at a 200 kHz span. The radio's number is the
    /// right one: used as the centre, the strongest bin on 40m landed on
    /// exactly 7,250.0 kHz, a real broadcast channel, which it does not if the
    /// VFO is used instead.
    /// </summary>
    public sealed class ScopeSweep
    {
        public required float[] BinsDb { get; init; }
        public long CentreHz { get; init; }
        public long SpanHz { get; init; }
        public bool OutOfRange { get; init; }

        /// <summary>
        /// The scope mode reported in the waveform header (field ④): 0=Center,
        /// 1=Fixed, 2=SCROLL-C, 3=SCROLL-F. This is the radio's live mode — it
        /// tracks front-panel changes — so the display can surface it.
        /// </summary>
        public byte Mode { get; init; }
    }

    /// <summary>
    /// Reassembles the IC-7300 CI-V spectrum scope (command <c>27 00</c>). Over
    /// USB the radio splits one sweep into 11 frames:
    ///   • order 01 — header only: <c>&lt;VFO&gt; &lt;order&gt; &lt;div&gt; &lt;mode&gt;
    ///     &lt;info×10&gt; &lt;oor&gt;</c>, no waveform bytes;
    ///   • orders 02–11 — <c>&lt;VFO&gt; &lt;order&gt; &lt;div&gt; &lt;bins…&gt;</c>,
    ///     where each bin is one byte 0x00 (bottom) … 0xA0 (reference line) and the
    ///     segments concatenate left edge → right edge into the 475-point trace.
    ///
    /// Pure and single-threaded by contract: <see cref="CivBusService"/> raises
    /// <c>UnsolicitedFrame</c> on one serial reader thread, so the caller feeds
    /// frames in arrival order and is handed a <see cref="ScopeSweep"/> back on
    /// the final segment. A dropped or out-of-sequence frame simply discards the
    /// in-flight sweep and waits for the next header — the display just skips a
    /// frame rather than tearing.
    /// </summary>
    public sealed class CivScopeAssembler
    {
        /// <summary>IC-7300 scope resolution: 475 waveform points per sweep.</summary>
        public const int BinCount = 475;

        // Waveform amplitude range: 0x00 = bottom of the scope, 0xA0 (160) = the
        // reference-level line at the top.
        private const int MaxAmplitude = 0xA0;

        private readonly List<byte> _bins = new(BinCount);
        private int _expectedOrder;   // next order byte expected (>=2); 0 = waiting for a header
        private int _division;        // total segments in this sweep (field ③)
        private long _centreHz;
        private long _spanHz;
        private bool _outOfRange;
        private byte _mode;

        /// <summary>
        /// Count of fully-assembled sweeps handed back since start. Paired with
        /// <see cref="SweepsDiscarded"/> it gives the scope's frame-drop rate,
        /// which the poll loop logs periodically so bus-contention stutter is a
        /// measured number rather than an eyeballed one.
        /// </summary>
        public long SweepsCompleted { get; private set; }

        /// <summary>
        /// Count of in-flight sweeps thrown away because a segment arrived
        /// dropped or out-of-order (a lost scope frame — almost always a
        /// solicited transaction stealing bus time mid-sweep). Does not count
        /// the benign "joined mid-stream, waiting for the next header" case.
        /// </summary>
        public long SweepsDiscarded { get; private set; }

        /// <summary>
        /// Feed one <c>27 00</c> frame. Returns a completed <see cref="ScopeSweep"/>
        /// when the last segment of a sweep arrives, otherwise null.
        /// </summary>
        public ScopeSweep? Add(CivFrame frame)
        {
            var d = frame.Data;   // [00 sub, VFO, order, div, …]
            if (d.Length < 4 || d[0] != CivProtocol.SubScopeWaveform)
                return null;

            int order = CivProtocol.BcdByte(d[2]);
            int div   = CivProtocol.BcdByte(d[3]);
            if (order < 1 || div < 1)
                return null;

            if (order == 1)
            {
                StartSweep(d, div);
                return null;
            }

            // A continuation before any header (we joined mid-sweep), or a
            // dropped / out-of-order segment: resync and wait for the next header.
            if (_expectedOrder == 0 || order != _expectedOrder || div != _division)
            {
                // Only a genuine mid-sweep loss counts as a discard; _expectedOrder
                // == 0 just means we haven't seen a header yet (benign resync).
                if (_expectedOrder != 0)
                    SweepsDiscarded++;
                _expectedOrder = 0;
                _bins.Clear();
                return null;
            }

            // Waveform bytes follow the 3-byte <VFO> <order> <div> preamble.
            for (int i = 4; i < d.Length; i++)
                _bins.Add(d[i]);

            if (order == div)
            {
                var sweep = BuildSweep();
                _expectedOrder = 0;
                SweepsCompleted++;
                return sweep;
            }

            _expectedOrder++;
            return null;
        }

        // Header frame (order 01): <VFO> <order> <div> <mode> <info×10> <oor>.
        // Data indices: d[1]=VFO, d[2]=order, d[3]=div, d[4]=mode,
        // d[5..15)=waveform info, d[15]=out-of-range.
        private void StartSweep(byte[] d, int div)
        {
            _bins.Clear();
            _division   = div;
            _mode       = d.Length > 4 ? d[4] : (byte)0;
            _outOfRange = d.Length > 15 && d[15] != 0;

            if (d.Length >= 15)
            {
                var info = new ReadOnlySpan<byte>(d, 5, 10);
                if (_mode == CivProtocol.ScopeModeCenter)
                {
                    // Center mode: centre frequency (5 BCD) + span (5 BCD). The
                    // span BCD decodes straight to Hz (2500 → 2.5 kHz …) and is
                    // the ± half-width, so the displayed span is twice it.
                    _centreHz = CivProtocol.DecodeBcd(info.Slice(0, 5));
                    _spanHz   = CivProtocol.DecodeBcd(info.Slice(5, 5)) * 2;
                }
                else
                {
                    // Fixed / scroll modes: lower edge + higher edge. Best-effort
                    // fallback — we enable Center mode, so this is rarely hit.
                    long lo = DecodeEdge(info.Slice(0, 5));
                    long hi = DecodeEdge(info.Slice(5, 5));
                    _centreHz = (lo + hi) / 2;
                    _spanHz   = hi - lo;
                }
            }

            _expectedOrder = 2;   // first waveform segment is order 02
        }

        // A lower/higher edge frequency. A negative lower edge is flagged by an
        // 'F' in the top nibble of the most-significant byte; mask it so
        // DecodeBcd doesn't read F as a digit. Sub-kHz digits are always 0.
        private static long DecodeEdge(ReadOnlySpan<byte> b)
        {
            Span<byte> tmp = stackalloc byte[5];
            b.CopyTo(tmp);
            bool negative = (tmp[4] >> 4) == 0x0F;
            if (negative) tmp[4] &= 0x0F;
            long v = CivProtocol.DecodeBcd(tmp);
            return negative ? -v : v;
        }

        // Convert the accumulated 0–160 amplitudes into a fixed-length dBFS-like
        // float array in −120…0, matching what the SDR path fed the frontend so
        // the SpectrumPanel colour map and dB sliders work unchanged. Defensive
        // about a short/long sweep so the frontend never sees a jagged array.
        private ScopeSweep BuildSweep()
        {
            int n = _bins.Count;
            var db = new float[BinCount];
            for (int i = 0; i < BinCount; i++)
            {
                int v = 0;
                if (n > 0)
                {
                    int src = n == BinCount ? i : i * n / BinCount;
                    v = _bins[src];
                }
                if (v > MaxAmplitude) v = MaxAmplitude;
                db[i] = (float)(v * (120.0 / MaxAmplitude) - 120.0);
            }
            return new ScopeSweep
            {
                BinsDb     = db,
                CentreHz   = _centreHz,
                SpanHz     = _spanHz,
                OutOfRange = _outOfRange,
                Mode       = _mode,
            };
        }
    }
}
