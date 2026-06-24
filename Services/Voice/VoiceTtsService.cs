using System.Speech.Synthesis;
using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Speaks short confirmation phrases ("Move to fourteen point zero seven
    /// four megahertz, successful") via Windows' built-in TTS engine after
    /// every voice command completes. Same SAPI 5 stack the recogniser uses,
    /// so audio output goes to whichever device Windows considers the default
    /// playback target. Construction is best-effort -- if SAPI's
    /// synthesiser can't be created (corrupt install, no voices, etc.), the
    /// service silently no-ops via SpeakAsync rather than throwing.
    ///
    /// Singleton -- holds a long-lived SpeechSynthesizer that the system
    /// caches voice/audio resources against.
    /// </summary>
    public sealed class VoiceTtsService : IDisposable
    {
        private readonly ILogger<VoiceTtsService> _logger;
        private SpeechSynthesizer? _synth;

        public VoiceTtsService(ILogger<VoiceTtsService> logger)
        {
            _logger = logger;
            try
            {
                _synth = new SpeechSynthesizer();
                _synth.SetOutputToDefaultAudioDevice();
                _logger.LogInformation(
                    "[VoiceTts] Synthesiser ready (voice={Voice})",
                    _synth.Voice?.Name ?? "(default)");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceTts] Failed to initialise speech synthesiser; confirmation announcements disabled");
                _synth = null;
            }
        }

        /// <summary>
        /// Speak the given text asynchronously. Cancels any in-flight speech
        /// (rapid successive PTT presses don't queue up several seconds of
        /// stale confirmations). No-op if the synthesiser failed to init
        /// or the text is empty.
        /// </summary>
        public void Speak(string text)
        {
            if (_synth == null) return;
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                _synth.SpeakAsyncCancelAll();
                _synth.SpeakAsync(text);
                _logger.LogInformation("[VoiceTts] Speak: '{Text}'", text);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceTts] Speak failed for: '{Text}'", text);
            }
        }

        public void Dispose()
        {
            try { _synth?.Dispose(); } catch { /* ignore */ }
            _synth = null;
        }
    }
}
