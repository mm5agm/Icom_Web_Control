using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Speech.Recognition;
using Yaesu_Web_Control.Hubs;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// In-process voice control via Windows SAPI 5 / System.Speech.Recognition.
    /// Replaces the parked Alexa work — see docs/VoiceControl/v1-plan.md.
    /// </summary>
    ///
    /// <remarks>
    /// Lifecycle:
    /// <list type="bullet">
    /// <item><c>StartAsync</c> (hosted service): attempts to construct the
    /// SAPI recogniser for <c>en-GB</c> and load <c>Grammars/Commands.en-GB.srgs</c>.
    /// Failures are logged but non-fatal — the rest of YWC still runs.</item>
    /// <item><c>StartListeningAsync</c> (called by VoiceController when the
    /// on-screen PTT button is pressed): wires audio input + begins
    /// recognition. State -> Listening.</item>
    /// <item>SAPI fires <c>SpeechRecognized</c> when a phrase matches a
    /// grammar rule. The handler extracts the semantic <c>intent</c> tag
    /// and any parameters, then hands off to <see cref="IntentDispatcher"/>.</item>
    /// <item><c>StopListening</c>: ends recognition. State -> Idle.</item>
    /// </list>
    /// SAPI's <c>SpeechRecognitionEngine</c> is not thread-safe. All engine
    /// operations are serialised through <see cref="_engineLock"/>.
    /// </remarks>
    public sealed class VoiceControlService : BackgroundService
    {
        private readonly ILogger<VoiceControlService> _logger;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly IntentDispatcher _intentDispatcher;
        private readonly IWebHostEnvironment _env;

        private readonly object _engineLock = new();
        private SpeechRecognitionEngine? _engine;
        private bool _audioWired;

        // Status is read by /api/voice/status; updated whenever state changes.
        // Volatile reference assignment is atomic in .NET so no lock needed
        // for the typical reader path.
        private VoiceStatusUpdate _status = new(VoiceState.Idle, null, null, null);
        public VoiceStatusUpdate CurrentStatus => _status;

        public VoiceControlService(
            ILogger<VoiceControlService> logger,
            IHubContext<RadioHub> hubContext,
            IntentDispatcher intentDispatcher,
            IWebHostEnvironment env)
        {
            _logger = logger;
            _hubContext = hubContext;
            _intentDispatcher = intentDispatcher;
            _env = env;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            TryInitialiseEngine();
            await base.StartAsync(cancellationToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            DisposeEngine();
            await base.StopAsync(cancellationToken);
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Nothing to do in the background loop — voice control is
            // entirely event-driven (SpeechRecognized) once the engine is
            // running. Just park until shutdown.
            return Task.Delay(Timeout.Infinite, stoppingToken);
        }

        /// <summary>
        /// Begin recognising. Called by the API endpoint when the PTT button
        /// is pressed. Idempotent — calling while already listening is a no-op.
        /// </summary>
        public async Task<bool> StartListeningAsync()
        {
            lock (_engineLock)
            {
                if (_engine == null)
                {
                    UpdateStatus(VoiceState.Error, error: "Speech recogniser not available (check Windows en-GB speech pack)");
                    return false;
                }

                try
                {
                    if (!_audioWired)
                    {
                        // Deferred until first start so YWC boots fine on
                        // machines without a microphone.
                        _engine.SetInputToDefaultAudioDevice();
                        _audioWired = true;
                    }
                    _engine.RecognizeAsync(RecognizeMode.Multiple);
                    UpdateStatus(VoiceState.Listening);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    // Already recognising — fine, idempotent.
                    UpdateStatus(VoiceState.Listening);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Voice] Failed to start recogniser");
                    UpdateStatus(VoiceState.Error, error: ex.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// Stop recognising. Called by the API endpoint when the PTT button
        /// is released. Idempotent.
        /// </summary>
        public Task StopListeningAsync()
        {
            lock (_engineLock)
            {
                try
                {
                    _engine?.RecognizeAsyncStop();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Voice] RecognizeAsyncStop threw (non-fatal)");
                }
                UpdateStatus(VoiceState.Idle);
            }
            return Task.CompletedTask;
        }

        // -----------------------------------------------------------------

        private void TryInitialiseEngine()
        {
            try
            {
                var culture = new CultureInfo("en-GB");
                var available = SpeechRecognitionEngine.InstalledRecognizers()
                    .Any(r => r.Culture.Name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase));
                if (!available)
                {
                    _logger.LogWarning(
                        "[Voice] en-GB SAPI recogniser not installed on this machine. " +
                        "Install via Settings → Time & Language → Speech.");
                    UpdateStatus(VoiceState.Error, error: "en-GB Windows speech pack not installed");
                    return;
                }

                var engine = new SpeechRecognitionEngine(culture);
                engine.SpeechRecognized += OnSpeechRecognized;
                engine.SpeechRecognitionRejected += OnSpeechRejected;
                engine.RecognizeCompleted += OnRecognizeCompleted;

                var grammarPath = Path.Combine(_env.ContentRootPath, "Grammars", "Commands.en-GB.srgs");
                if (!File.Exists(grammarPath))
                {
                    _logger.LogWarning(
                        "[Voice] Grammar file not found at {Path}. The recogniser is constructed but " +
                        "won't match anything until a grammar is added.",
                        grammarPath);
                    UpdateStatus(VoiceState.Error, error: $"Grammar file missing: {grammarPath}");
                    // Keep the engine — Step 2 will add the grammar file and it
                    // will load on next process start.
                    _engine = engine;
                    return;
                }

                var grammar = new Grammar(grammarPath);
                engine.LoadGrammar(grammar);
                _engine = engine;
                _logger.LogInformation(
                    "[Voice] SAPI recogniser ready (culture={Culture}, grammar={Grammar})",
                    culture.Name, Path.GetFileName(grammarPath));
                UpdateStatus(VoiceState.Idle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] Failed to initialise speech recogniser");
                UpdateStatus(VoiceState.Error, error: ex.Message);
            }
        }

        private void DisposeEngine()
        {
            lock (_engineLock)
            {
                try
                {
                    _engine?.RecognizeAsyncCancel();
                    _engine?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Voice] Engine dispose threw (non-fatal)");
                }
                _engine = null;
                _audioWired = false;
            }
        }

        private async void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
        {
            // Pull the semantic intent from the SRGS <tag>out.intent="..."</tag>.
            // If the grammar doesn't tag anything, fall back to logging the raw
            // phrase only — useful while authoring the grammar.
            string heard = e.Result.Text;
            string? intent = TryGetSemanticString(e.Result.Semantics, "intent");

            if (intent == null)
            {
                _logger.LogInformation("[Voice] Heard '{Heard}' (no intent tag)", heard);
                UpdateStatus(VoiceState.Heard, heard: heard);
                return;
            }

            // Flatten semantic parameters (everything other than the intent
            // name itself) into a string-keyed dictionary for the dispatcher.
            var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in e.Result.Semantics)
            {
                if (string.Equals(key.Key, "intent", StringComparison.OrdinalIgnoreCase))
                    continue;
                args[key.Key] = key.Value?.Value ?? string.Empty;
            }

            UpdateStatus(VoiceState.Heard, heard: heard, intent: intent);
            UpdateStatus(VoiceState.Executing, heard: heard, intent: intent);
            try
            {
                await _intentDispatcher.DispatchAsync(intent, args);
                UpdateStatus(VoiceState.Idle, heard: heard, intent: intent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Voice] Intent dispatch failed for '{Intent}'", intent);
                UpdateStatus(VoiceState.Error, heard: heard, intent: intent, error: ex.Message);
            }
        }

        private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
        {
            // SAPI heard SOMETHING but it didn't match any grammar rule.
            // Useful diagnostic — surface the best alternative to the user.
            string? best = e.Result?.Alternates?.FirstOrDefault()?.Text;
            _logger.LogInformation("[Voice] Rejected (best alt: '{Best}')", best ?? "<none>");
            UpdateStatus(VoiceState.Unrecognised, heard: best);
        }

        private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
        {
            // Fires when RecognizeAsyncStop() finishes draining. Reset to Idle
            // so the UI button settles back to grey.
            if (_status.State == VoiceState.Listening)
                UpdateStatus(VoiceState.Idle);
        }

        private static string? TryGetSemanticString(SemanticValue? semantics, string key)
        {
            if (semantics == null) return null;
            if (semantics.ContainsKey(key))
                return semantics[key]?.Value?.ToString();
            return null;
        }

        private void UpdateStatus(
            VoiceState state,
            string? heard = null,
            string? intent = null,
            string? error = null)
        {
            // Preserve previous LastHeard/LastIntent unless the caller passes
            // something new, so transient states (Heard -> Executing -> Idle)
            // keep showing the most recent phrase.
            var prev = _status;
            var update = new VoiceStatusUpdate(
                state,
                heard ?? prev.LastHeard,
                intent ?? prev.LastIntent,
                error
            );
            _status = update;

            // Fire-and-forget SignalR broadcast — clients react asynchronously.
            // Failure to broadcast doesn't block voice processing.
            try
            {
                _hubContext.Clients.All.SendAsync("VoiceStatusUpdate", update);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Voice] SignalR broadcast failed (non-fatal)");
            }
        }
    }
}
