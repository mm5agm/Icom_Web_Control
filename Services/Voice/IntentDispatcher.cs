using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Maps a recognised semantic intent (string name + parameter dictionary
    /// from the SRGS grammar's <c>out.intent</c> tags) to the existing CAT
    /// helper methods that already drive the radio for clicks-and-knobs.
    /// </summary>
    ///
    /// <remarks>
    /// Step 1 (this commit) ships an empty skeleton that just logs the intent
    /// and parameters. Step 3 of the v1 plan wires the actual CAT dispatch.
    /// Recognised intents (v1, from <c>docs/VoiceControl/v1-plan.md</c>):
    /// <c>SetFrequency</c>, <c>SetBand</c>, <c>SetMode</c>, <c>SwapVFO</c>,
    /// <c>NudgeFrequency</c>.
    /// </remarks>
    public sealed class IntentDispatcher
    {
        private readonly ILogger<IntentDispatcher> _logger;

        public IntentDispatcher(ILogger<IntentDispatcher> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Dispatch a recognised intent. Returns true if the intent was
        /// recognised (regardless of whether the radio actually accepted
        /// the change); false if the intent name is unknown.
        /// </summary>
        public async Task<bool> DispatchAsync(
            string intent,
            IReadOnlyDictionary<string, object> parameters,
            CancellationToken cancellationToken = default)
        {
            // Step 1 placeholder: log only. Step 3 will switch on intent and
            // call the matching CatController helper or CatMultiplexerService
            // method directly.
            _logger.LogInformation(
                "[IntentDispatcher] (stub) intent={Intent} params={@Params}",
                intent, parameters);
            await Task.CompletedTask;
            return true;
        }
    }
}
