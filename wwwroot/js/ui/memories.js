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
        dialog.style.left = newLeft + 'px';
        dialog.style.top  = newTop  + 'px';
        dialog.style.margin = '0';
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
        startX = e.clientX; startY = e.clientY;
        const r = dialog.getBoundingClientRect();
        origLeft = r.left; origTop = r.top;
        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup',   onEnd);
        e.preventDefault();
    });

    handle.addEventListener('touchstart', e => {
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

function _restorePosition(dialog) {
    try {
        const saved = localStorage.getItem(MEM_PANEL_KEY + '_pos');
        if (!saved) return;
        const { left, top } = JSON.parse(saved);
        dialog.style.left   = Math.max(0, Math.min(window.innerWidth  - 320, left)) + 'px';
        dialog.style.top    = Math.max(0, Math.min(window.innerHeight - 200, top))  + 'px';
        dialog.style.margin = '0';
    } catch { /* ignore */ }
}
