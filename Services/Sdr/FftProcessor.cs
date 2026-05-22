// Yaesu Web Control – FFT Processor
// Pure functions only. No UI, no DOM, no WebSocket, no SignalR, no side effects.
// Converts a buffer of interleaved float IQ samples into a dBFS spectrum array.

using MathNet.Numerics.IntegralTransforms;

namespace Yaesu_Web_Control.Services.Sdr
{
    internal static class FftProcessor
    {
        // Pre-computed Hann window coefficients, cached per FFT size.
        private static readonly Dictionary<int, float[]> _hannWindowCache = new();

        /// <summary>
        /// Converts <paramref name="iqSamples"/> (I, Q interleaved floats) into a
        /// dBFS power spectrum of length <paramref name="fftSize"/>.
        ///
        /// Output bins are FFT-shifted so bin 0 = most-negative frequency (−SR/2)
        /// and bin N−1 = most-positive frequency (+SR/2 − resolution), matching
        /// what a spectrum analyser displays with DC at centre.
        /// </summary>
        /// <param name="iqSamples">
        ///   Float buffer of length ≥ fftSize × 2 containing (I₀, Q₀, I₁, Q₁ …).
        /// </param>
        /// <param name="fftSize">Number of IQ samples to transform (must be a power of two).</param>
        /// <returns>Array of <paramref name="fftSize"/> floats in dBFS.</returns>
        public static float[] ComputeSpectrum(float[] iqSamples, int fftSize)
        {
            float[] window  = GetHannWindow(fftSize);
            var     complex = new MathNet.Numerics.Complex32[fftSize];

            for (int i = 0; i < fftSize; i++)
            {
                float I = iqSamples[i * 2]     * window[i];
                float Q = iqSamples[i * 2 + 1] * window[i];
                complex[i] = new MathNet.Numerics.Complex32(I, Q);
            }

            // Forward FFT — asymmetric scaling means the transform is not normalised;
            // we normalise manually below so 0 dBFS = full-scale signal.
            Fourier.Forward(complex, FourierOptions.AsymmetricScaling);

            float[]  bins       = new float[fftSize];
            float    normFactor = 1.0f / fftSize;

            for (int i = 0; i < fftSize; i++)
            {
                // FFT shift: swap halves so the DC bin lands in the centre of the output.
                int   shiftedIdx = (i + fftSize / 2) % fftSize;
                float magnitude  = complex[shiftedIdx].Magnitude * normFactor;

                // +1e-10f guards against log10(0).
                bins[i] = 20.0f * MathF.Log10(magnitude + 1e-10f);
            }

            return bins;
        }

        // ── Private helpers ──────────────────────────────────────────────────────

        private static float[] GetHannWindow(int size)
        {
            if (_hannWindowCache.TryGetValue(size, out float[]? cached))
                return cached;

            var window = new float[size];
            float denominator = size - 1;

            for (int i = 0; i < size; i++)
                window[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / denominator));

            _hannWindowCache[size] = window;
            return window;
        }
    }
}
