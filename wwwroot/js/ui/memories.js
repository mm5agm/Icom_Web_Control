// Floating memories panel — open/close, drag, tile rendering, recall on click.

const MEM_PANEL_KEY = 'memoriesPanel';

let _memories = [];
let _panelOpen = false;

export function initMemoriesPanel() {
    const dialog = document.getElementById('memoriesDialog');
    const header = document.getElementById('memoriesHeader');
    if (!dialog) return;

    // Restore saved position
    _restorePosition(dialog);

    // Drag support (mouse + touch) on header
    _makeDraggable(dialog, header);

    // Close button
    document.getElementById('memoriesClose')?.addEventListener('click', closeMemoriesPanel);

    // Open button (toolbar)
    document.getElementById('memBtn')?.addEventListener('click', openMemoriesPanel);

    // Reload when dialog opened
    dialog.addEventListener('toggle', () => {
        if (dialog.open) _loadAndRender();
    });
}

export function openMemoriesPanel() {
    const dialog = document.getElementById('memoriesDialog');
    if (!dialog) return;
    if (!dialog.open) {
        dialog.show();   // non-modal so the rest of the UI stays interactive
        _panelOpen = true;
        _loadAndRender();
    }
}

export function closeMemoriesPanel() {
    const dialog = document.getElementById('memoriesDialog');
    if (dialog && dialog.open) {
        dialog.close();
        _panelOpen = false;
    }
}

async function _loadAndRender() {
    const container = document.getElementById('memoriesTiles');
    if (!container) return;

    container.innerHTML = '<div class="text-muted small p-2">Loading…</div>';

    try {
        const resp = await fetch('/api/memory');
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        _memories = await resp.json();
    } catch (e) {
        container.innerHTML = `<div class="text-danger small p-2">Failed to load memories: ${e.message}</div>`;
        return;
    }

    _renderTiles(container);
}

function _renderTiles(container) {
    if (_memories.length === 0) {
        container.innerHTML =
            '<div class="text-muted small p-3">No memories saved yet. ' +
            '<a href="/Memories" target="_blank">Open the Memories editor</a> to add some.</div>';
        return;
    }

    const frag = document.createDocumentFragment();
    for (const mem of _memories) {
        const tile = document.createElement('button');
        tile.type = 'button';
        tile.className = 'mem-tile';
        tile.title = `Recall: ${mem.label || 'Memory ' + mem.id}`;
        tile.dataset.memId = mem.id;

        const mhz = mem.frequencyHz >= 1000
            ? (mem.frequencyHz / 1e6).toFixed(mem.frequencyHz % 1000 === 0 ? 3 : 6).replace(/\.?0+$/, '')
            : (mem.frequencyHz / 1e6).toFixed(6);

        tile.innerHTML =
            `<span class="mem-tile-label">${_esc(mem.label || ('Mem ' + mem.id))}</span>` +
            `<span class="mem-tile-freq">${mhz} MHz</span>` +
            `<span class="mem-tile-mode">${_esc(mem.mode)}</span>`;

        tile.addEventListener('click', () => _recallMemory(mem.id));
        frag.appendChild(tile);
    }

    container.innerHTML = '';
    container.appendChild(frag);
}

async function _recallMemory(id) {
    const tile = document.querySelector(`.mem-tile[data-mem-id="${id}"]`);
    if (tile) {
        tile.classList.add('mem-tile-active');
        setTimeout(() => tile.classList.remove('mem-tile-active'), 800);
    }
    try {
        await fetch(`/api/memory/${id}/recall`, { method: 'POST' });
    } catch (e) {
        console.error('Memory recall failed:', e);
    }
}

function _esc(str) {
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

// ── Drag ─────────────────────────────────────────────────────────────────────

function _makeDraggable(dialog, handle) {
    if (!handle) return;

    let startX, startY, origLeft, origTop;

    function onMove(cx, cy) {
        let newLeft = origLeft + (cx - startX);
        let newTop  = origTop  + (cy - startY);
        // Clamp inside viewport
        newLeft = Math.max(0, Math.min(window.innerWidth  - dialog.offsetWidth,  newLeft));
        newTop  = Math.max(0, Math.min(window.innerHeight - dialog.offsetHeight, newTop));
        dialog.style.left      = newLeft + 'px';
        dialog.style.top       = newTop  + 'px';
        dialog.style.margin    = '0';
        dialog.style.transform = 'none';
    }

    function onEnd() {
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup',   onEnd);
        document.removeEventListener('touchmove', onTouchMove);
        document.removeEventListener('touchend',  onEnd);
        _savePosition(dialog);
    }

    function onMouseMove(e) { onMove(e.clientX, e.clientY); }
    function onTouchMove(e) { onMove(e.touches[0].clientX, e.touches[0].clientY); }

    handle.addEventListener('mousedown', e => {
        if (e.button !== 0) return;
        if (e.target.closest('a, button')) return;
        startX = e.clientX; startY = e.clientY;
        const r = dialog.getBoundingClientRect();
        origLeft = r.left; origTop = r.top;
        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup',   onEnd);
        e.preventDefault();
    });

    handle.addEventListener('touchstart', e => {
        if (e.target.closest('a, button')) return;
        startX = e.touches[0].clientX; startY = e.touches[0].clientY;
        const r = dialog.getBoundingClientRect();
        origLeft = r.left; origTop = r.top;
        document.addEventListener('touchmove', onTouchMove, { passive: false });
        document.addEventListener('touchend',  onEnd);
    }, { passive: true });
}

function _savePosition(dialog) {
    const r = dialog.getBoundingClientRect();
    localStorage.setItem(MEM_PANEL_KEY + '_pos',
        JSON.stringify({ left: r.left, top: r.top }));
}

// ── Import / Export ──────────────────────────────────────────────────────────

function _setToolbarBusy(busy, status) {
    ['memImportReplaceBtn', 'memImportAddBtn', 'memExportBtn'].forEach(id => {
        const el = document.getElementById(id);
        if (el) el.disabled = busy;
    });
    const s = document.getElementById('memToolbarStatus');
    if (s) s.textContent = status || '';
}

window.importMemories = async function (mode) {
    if (!confirm(mode === 'replace'
        ? 'Replace ALL app memories with channels from the radio?'
        : 'Add radio channels to existing app memories?')) return;
    _setToolbarBusy(true, 'Reading radio…');
    try {
        const resp = await fetch('/api/memory/import-radio', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ mode })
        });
        if (resp.ok) {
            const data = await resp.json();
            _setToolbarBusy(false, `✓ Imported ${data.imported}`);
            await _loadAndRender();
        } else {
            _setToolbarBusy(false, '✗ Import failed');
        }
    } catch (e) {
        _setToolbarBusy(false, '✗ Error');
        console.error('Memory import failed:', e);
    }
};

window.exportMemories = async function () {
    if (!confirm('Write app memories to radio channels? Existing radio channels will be overwritten.')) return;
    _setToolbarBusy(true, 'Writing to rig…');
    try {
        const resp = await fetch('/api/memory/export-radio', { method: 'POST' });
        if (resp.ok) {
            const data = await resp.json();
            _setToolbarBusy(false, `✓ Wrote ${data.written}`);
        } else {
            _setToolbarBusy(false, '✗ Export failed');
        }
    } catch (e) {
        _setToolbarBusy(false, '✗ Error');
        console.error('Memory export failed:', e);
    }
};

// ── Position persistence ──────────────────────────────────────────────────────

function _restorePosition(dialog) {
    try {
        const saved = localStorage.getItem(MEM_PANEL_KEY + '_pos');
        if (!saved) return;
        const { left, top } = JSON.parse(saved);
        // Discard saved position if it would put the dialog mostly off-screen
        if (left < 0 || left > window.innerWidth - 100) return;
        dialog.style.left      = Math.max(0, Math.min(window.innerWidth  - 320, left)) + 'px';
        dialog.style.top       = Math.max(0, Math.min(window.innerHeight - 200, top))  + 'px';
        dialog.style.margin    = '0';
        dialog.style.transform = 'none';
    } catch { /* ignore */ }
}
