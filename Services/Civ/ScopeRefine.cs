using System;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// The Refine steps for the radio's scope: how much each new sweep counts
    /// in the average <see cref="RadioWebControl.Core.Services.Spectrum.SpectrumRefiner"/>
    /// keeps. Step 0 is the raw sweeps.
    /// </summary>
    /// <remarks>
    /// Lighter than YWC's steps for the FTdx101's ACC scope (1, .6, .45, .3,
    /// .2, .12), because the IC-7300 sends far fewer sweeps: about 3 a second
    /// over CI-V at 19200 baud against the ACC scope's 25. A weight w takes
    /// about 1/(sweeps a second × w) seconds to follow a change, so 0.3 here is
    /// about a second, and anything smaller would lag a signal that comes and
    /// goes. Measured 2026-11-12 on 20m FT8, 20 kHz span, REF +10 dB, peaks
    /// over floor roughness: raw 15.3, 0.5 gave 23.4, 0.3 gave 28.9.
    /// </remarks>
    public static class ScopeRefine
    {
        public static readonly double[] Weights = { 1.0, 0.75, 0.6, 0.5, 0.4, 0.3 };

        public const int DefaultLevel = 3;

        public static int MaxLevel => Weights.Length - 1;

        public static int Clamp(int level) => Math.Clamp(level, 0, MaxLevel);

        public static double WeightFor(int level) => Weights[Clamp(level)];
    }
}
