// Radio Web Control - Spot Map overlays: state and province borders, and
// Maidenhead grid squares. Pure functions only, so they can be tested
// without a browser; spot-map-page.js draws what they return.
// Edit it here, in the core, never in a wwwroot copy.

// region-data.js text -> [{ cc, abbr, name, lat, lon, rings: [[[lat, lon], ...], ...] }]
export function decodeRegions(text) {
    const out = [];
    for (const line of String(text).split('\n')) {
        const f = line.trim().split('|');
        if (f.length < 6) continue;
        const rings = f[5].split(';').map(code => {
            const n = code.split(',').map(Number);
            const pts = [];
            let x = 0, y = 0;
            for (let i = 0; i + 1 < n.length; i += 2) {
                x += n[i]; y += n[i + 1];
                pts.push([y / 100, x / 100]);
            }
            return pts;
        }).filter(r => r.length >= 3);
        out.push({ cc: f[0], abbr: f[1], name: f[2], lat: Number(f[3]), lon: Number(f[4]), rings });
    }
    return out;
}

// Maidenhead locator of a point: 2 (field), 4 (square) or 6 (subsquare) characters.
export function locatorAt(lat, lon, chars = 4) {
    let x = ((lon + 180) % 360 + 360) % 360;   // 0..360, wraps panned-past-180 longitudes
    let y = Math.min(Math.max(lat + 90, 0), 179.999999);
    const A = 'A'.charCodeAt(0);
    let s = String.fromCharCode(A + Math.floor(x / 20), A + Math.floor(y / 10));
    if (chars >= 4) {
        x %= 20; y %= 10;
        s += String(Math.floor(x / 2)) + String(Math.floor(y));
        if (chars >= 6) {
            x %= 2; y %= 1;
            s += String.fromCharCode(A + 32 + Math.floor(x * 12), A + 32 + Math.floor(y * 24));
        }
    }
    return s;
}

// The three grid sizes: fields 20 x 10 degrees, squares 2 x 1, subsquares 5' x 2.5'.
export const GRID_LEVELS = [
    { chars: 2, lon: 20, lat: 10 },
    { chars: 4, lon: 2, lat: 1 },
    { chars: 6, lon: 1 / 12, lat: 1 / 24 },
];

// The finest grid whose cells are still at least minPx wide on screen.
// Leaflet's world is 256 px wide at zoom 0, doubling with each zoom step.
export function gridLevelFor(zoom, minPx = 44) {
    const pxPerDeg = 256 * 2 ** zoom / 360;
    let level = GRID_LEVELS[0];
    for (const l of GRID_LEVELS) if (l.lon * pxPerDeg >= minPx) level = l;
    return level;
}

// Lines and labels for the grid inside a view.
// bounds: { south, west, north, east } in degrees; west/east may pass +-180.
// Returns { level, lines: [[[lat, lon], [lat, lon]], ...], labels: [{ lat, lon, text }] }.
export function gridFor(bounds, zoom, maxCells = 2500) {
    const level = gridLevelFor(zoom);
    const south = Math.max(-90, Math.floor(bounds.south / level.lat) * level.lat);
    const north = Math.min(90, Math.ceil(bounds.north / level.lat) * level.lat);
    const west = Math.floor((bounds.west + 180) / level.lon) * level.lon - 180;
    const east = Math.ceil((bounds.east + 180) / level.lon) * level.lon - 180;
    const cols = Math.round((east - west) / level.lon), rows = Math.round((north - south) / level.lat);
    if (cols <= 0 || rows <= 0 || cols * rows > maxCells) return { level, lines: [], labels: [] };
    const lines = [], labels = [];
    for (let i = 0; i <= cols; i++) {
        const lon = west + i * level.lon;
        lines.push([[south, lon], [north, lon]]);
    }
    for (let j = 0; j <= rows; j++) {
        const lat = south + j * level.lat;
        lines.push([[lat, west], [lat, east]]);
    }
    for (let i = 0; i < cols; i++) {
        for (let j = 0; j < rows; j++) {
            const lat = south + (j + 0.5) * level.lat, lon = west + (i + 0.5) * level.lon;
            labels.push({ lat, lon, text: locatorAt(lat, lon, level.chars) });
        }
    }
    return { level, lines, labels };
}
