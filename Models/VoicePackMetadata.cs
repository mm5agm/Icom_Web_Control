using System.Text.Json.Serialization;

namespace Icom_Web_Control.Models
{
    /// <summary>
    /// Optional metadata alongside a voice language pack, persisted as
    /// Commands.&lt;culture&gt;.meta.json next to Commands.&lt;culture&gt;.json.
    /// Absent metadata is not an error — packs without it just show as
    /// "Unknown author / no description" wherever this is surfaced.
    /// See docs/VoiceControl/language-pack-manager-design.md §1.4.
    /// </summary>
    public sealed class VoicePackMetadata
    {
        [JsonPropertyName("author")]
        public string Author { get; set; } = "";

        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        /// <summary>BCP-47 culture tag, e.g. "en-GB". Must match the pack's filenames.</summary>
        [JsonPropertyName("locale")]
        public string Locale { get; set; } = "";

        [JsonPropertyName("dateCreated")]
        public DateTimeOffset DateCreated { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("dateModified")]
        public DateTimeOffset DateModified { get; set; } = DateTimeOffset.UtcNow;
    }
}
