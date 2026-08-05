// Loads accessible labels from /i18n/labels.json and applies them to
// any element that has a data-a11y-key attribute.
// Users can override labels by editing the file (or the AppData copy).

let _labels = null;

export async function loadLabels() {
    try {
        const resp = await fetch('/i18n/labels.json', { cache: 'no-cache' });
        if (resp.ok) _labels = await resp.json();
    } catch {
        // Non-fatal — built-in aria-labels remain as fallback.
    }
    applyAll();
}

// Split at the FIRST dot only: labels.json is two levels deep, section then
// key, and a key may itself contain dots ("vfo" → "a.frequency"). Labels.cshtml.cs
// splits the same way when it reads and writes the file, so keeping the two in
// step is what makes the editor round-trip. Walking every dot instead — which
// this used to do — left every vfo.* label silently unapplied.
function resolveKey(key) {
    if (!_labels) return null;
    const dot = key.indexOf('.');
    if (dot < 0) return null;
    const section = _labels[key.slice(0, dot)];
    if (section == null || typeof section !== 'object') return null;
    const label = section[key.slice(dot + 1)];
    return typeof label === 'string' ? label : null;
}

function applyAll() {
    document.querySelectorAll('[data-a11y-key]').forEach(el => {
        const label = resolveKey(el.dataset.a11yKey);
        if (label) {
            el.setAttribute('aria-label', label);
            // Sync title for non-canvas interactive elements (buttons, links) where
            // NVDA reads title as a tooltip; canvases are aria-hidden so title is irrelevant for them.
            if (el.hasAttribute('title')) el.setAttribute('title', label);
        }
    });
}
