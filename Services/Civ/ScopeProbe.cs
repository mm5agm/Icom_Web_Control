using System;
using System.Collections.Generic;
using System.Linq;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// A numeric health check on the radio's scope trace, logged once per run.
    ///
    /// The counterpart of Yaesu Web Control's SpectrumProbe, and here for the
    /// same reason: SpectrumPanel applies the Low/High/Gain sliders and a
    /// colour map before anything reaches the eye, so a screenshot cannot tell
    /// a real carrier at the tuned frequency from an artefact of the radio's
    /// own scope. Averaging a few seconds of sweeps and printing the numbers
    /// can.
    ///
    /// Note what this measures and what it does not. IWC has no SDR - the
    /// SDRplay backend was removed in July 2026 - so these bins are the
    /// IC-7300's own scope, already converted to a display scale by the radio
    /// before we ever see them. Anything found here is the radio's, not ours,
    /// and cannot be fixed the way YWC's DC offset was.
    /// </summary>
    public sealed class ScopeProbe
    {
        private readonly int      _targetSweeps;
        private readonly int      _skipSweeps;
        private readonly double[] _sum = new double[CivScopeAssembler.BinCount];
        private int    _skipped;
        private int    _sweeps;
        private bool   _reported;
        private long   _spanHz;
        private long   _centreHz;
        private byte   _mode;

        public ScopeProbe(int targetSweeps = 40, int skipSweeps = 10)
        {
            _targetSweeps = targetSweeps;
            _skipSweeps   = skipSweeps;
        }

        /// <summary>Accumulate one sweep. Returns true on the sweep that
        /// completes the average, after which the probe is inert.</summary>
        public bool Add(ScopeSweep sweep)
        {
            if (_reported) return false;
            // The first sweeps after the scope is switched on can arrive while
            // the radio is still settling its reference level.
            if (_skipped < _skipSweeps) { _skipped++; return false; }
            if (sweep.BinsDb.Length != _sum.Length) return false;

            for (int i = 0; i < _sum.Length; i++) _sum[i] += sweep.BinsDb[i];

            _spanHz   = sweep.SpanHz;
            _centreHz = sweep.CentreHz;
            _mode     = sweep.Mode;
            return ++_sweeps >= _targetSweeps;
        }

        /// <summary>
        /// One-shot report. The averaging is done in the radio's own display
        /// units, not in power: BinsDb is a linear remap of the scope's 0-160
        /// amplitude byte onto -120..0, so it is already log-like and turning
        /// it back into a power average would invent a precision the radio
        /// never sent.
        /// </summary>
        public IEnumerable<string> Format()
        {
            _reported = true;

            int      n  = _sum.Length;
            int      mid = n / 2;
            double[] db = new double[n];
            for (int i = 0; i < n; i++) db[i] = _sum[i] / _sweeps;

            double[] sorted = (double[])db.Clone();
            Array.Sort(sorted);
            double floorDb = sorted[n / 2];

            double perBin = _spanHz > 0 ? (double)_spanHz / n : 0;

            yield return $"SCOPEPROBE sweeps={_sweeps} bins={n} centre={_centreHz} Hz " +
                         $"span={_spanHz} Hz bin={perBin:0.0} Hz floor={floorDb:0.0} " +
                         $"mode={ScopeModeLabel(_mode)}";

            // The middle bin is the centre the radio reported, which is NOT
            // the VFO: in SSB the IC-7300 centres on the middle of the
            // passband, 1.5 kHz below the carrier on 40m LSB. So this measures
            // the middle of what is being listened to rather than the tuned
            // frequency itself, and in Fixed or Scroll it is only wherever the
            // band segment happens to be centred.
            yield return $"SCOPEPROBE centre excess {db[mid] - floorDb:+0.0;-0.0} " +
                         $"(neighbours {db[mid - 1] - floorDb:+0.0;-0.0} / " +
                         $"{db[mid + 1] - floorDb:+0.0;-0.0})" +
                         (_mode == 0 ? "" : "  -- not Center mode, centre bin is not the VFO");

            const int buckets = 32;
            var shape = new List<string>(buckets);
            for (int b = 0; b < buckets; b++)
            {
                int lo = b * n / buckets;
                int hi = (b + 1) * n / buckets;
                double sum = 0;
                for (int k = lo; k < hi; k++) sum += db[k];
                shape.Add($"{sum / (hi - lo) - floorDb:+0;-0;0}");
            }
            yield return "SCOPEPROBE shape(rel floor, low to high across span): " + string.Join(" ", shape);

            var top = Enumerable.Range(0, n)
                                .OrderByDescending(i => db[i])
                                .Take(5)
                                .Select(i => $"{(i - mid) * perBin / 1000.0:+0.0;-0.0;0}kHz={db[i]:0.0}");
            yield return "SCOPEPROBE strongest: " + string.Join("  ", top);
        }

        private static string ScopeModeLabel(byte mode) => mode switch
        {
            0 => "CENT",
            1 => "FIX",
            2 => "SCROLL-C",
            3 => "SCROLL-F",
            _ => $"?{mode}",
        };
    }
}
