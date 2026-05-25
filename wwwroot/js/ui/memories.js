// Floating memories panel — open/close, drag, tile rendering, recall on click.

const MEM_PANEL_KEY = 'memoriesPanel';

// Replaces native confirm() to avoid the "localhost:8080 says" browser header.
function _memConfirm(message) {
    return new Promise(resolve => {
        const dlg = document.createElement('dialog');
        dlg.style.cssText = [
            'border-radius:10px', 'border:2px solid #555', 'background:#1e1e2e',
            'color:#e0e0e0', 'padding:20px 24px', 'max-width:420px', 'width:90vw',
            'z-index:10001', 'box-shadow:0 10px 40px rgba(0,0,0,0.7)'
        ].join(';');
        dlg.innerHTML =
            `<p style="margin:0 0 18px;white-space:pre-wrap;font-size:0.88rem;line-height:1.5">${
                _esc(message)
            }</p>` +
            `<div style="display:flex;justify-content:flex-end;gap:8px">` +
            `<button data-r="0" style="padding:4px 14px;border-radius:5px;border:1px solid #666;background:#2d2d44;color:#ccc;cursor:pointer;font-size:0.85rem">Cancel</button>` +
            `<button data-r="1" style="padding:4px 14px;border-radius:5px;border:1px solid #4a9;background:#2a5a3a;color:#cfe;cursor:pointer;font-size:0.85rem">OK</button>` +
            `</div>`;
        document.body.appendChild(dlg);
        dlg.showModal();
        function finish(v) { dlg.close(); dlg.remove(); resolve(v); }
        dlg.querySelectorAll('button').forEach(b =>
            b.addEventListener('click', () => finish(b.dataset.r === '1')));
        dlg.addEventListener('cancel', () => finish(false));
    });
}

let _memories = [];
let _panelOpen = false;
let _banks = [];

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

    // Refresh button
    document.getElementById('memoriesRefresh')?.addEventListener('click', _loadAndRender);

    // Open button (toolbar)
    document.getElementById('memBtn')?.addEventListener('click', openMemoriesPanel);

    // Bank switcher
    document.getElementById('memBankSelect')?.addEventListener('change', e => _switchBank(e.target.value));

    // Reload when dialog opened
    dialog.addEventListener('toggle', () => {
        if (dialog.open) { _loadBanks(); _loadAndRender(); }
    });

    // Refresh when the user returns to this tab (e.g. after editing in /Memories)
    document.addEventListener('visibilitychange', () => {
        if (!document.hidden && dialog.open) _loadAndRender();
    });

    // Allow non-module scripts (e.g. site.js) to trigger a refresh after saving
    window.refreshMemoriesPanel = () => {
        if (dialog.open) _loadAndRender();
    };
}

export function openMemoriesPanel() {
    const dialog = document.getElementById('memoriesDialog');
    if (!dialog) return;
    if (!dialog.open) {
        dialog.show();   // non-modal so the rest of the UI stays interactive
        _panelOpen = true;
        _loadBanks();
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
        if (!resp.ok) throw new Error(`Server returned HTTP ${resp.status}`);
        _memories = await resp.json();
        _renderTiles(container);
    } catch (e) {
        const msg = e instanceof TypeError
            ? 'Server not responding — is Yaesu Web Control still running?'
            : `Failed to load memories: ${e.message}`;
        container.innerHTML = `<div class="text-danger small p-2">${msg}</div>`;
    }
}

async function _loadBanks() {
    const sel = document.getElementById('memBankSelect');
    if (!sel) return;
    try {
        const resp = await fetch('/api/memorybank');
        if (!resp.ok) return;
        _banks = await resp.json();
        sel.innerHTML = '<option value="">Banks…</option>';
        for (const name of _banks) {
            const opt = document.createElement('option');
            opt.value = name;
            opt.textContent = name;
            sel.appendChild(opt);
        }
        sel.style.display = _banks.length === 0 ? 'none' : '';
    } catch { /* ignore — banks are optional */ }
}

async function _switchBank(name) {
    if (!name) return;
    try {
        const resp = await fetch(`/api/memorybank/${encodeURIComponent(name)}/load`, { method: 'POST' });
        if (resp.ok) {
            await _loadAndRender();
        } else {
            console.error('Bank switch failed: server returned', resp.status);
        }
    } catch (e) {
        console.error('Bank switch failed:', e);
    }
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
        const item = document.createElement('div');
        item.setAttribute('role', 'listitem');

        const tile = document.createElement('button');
        tile.type = 'button';
        tile.className = 'mem-tile';
        const label = mem.label || ('Mem ' + mem.id);
        tile.title = `Recall: ${label}`;
        tile.dataset.memId = mem.id;

        const mhz = mem.frequencyHz >= 1000
            ? (mem.frequencyHz / 1e6).toFixed(mem.frequencyHz % 1000 === 0 ? 3 : 6).replace(/\.?0+$/, '')
            : (mem.frequencyHz / 1e6).toFixed(6);

        tile.setAttribute('aria-label', `Recall ${label}, ${mhz} MHz, ${mem.mode}`);
        tile.innerHTML =
            `<span class="mem-tile-label" aria-hidden="true">${_esc(label)}</span>` +
            `<span class="mem-tile-freq" aria-hidden="true">${mhz} MHz</span>` +
            `<span class="mem-tile-mode" aria-hidden="true">${_esc(mem.mode)}</span>`;

        tile.addEventListener('click', () => _recallMemory(mem.id));
        item.appendChild(tile);
        frag.appendChild(item);
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

const _TOOLBAR_BTNS = ['memImportReplaceBtn', 'memImportAddBtn', 'memExportReplaceBtn', 'memExportAddBtn'];

function _setToolbarBusy(busy, status) {
    _TOOLBAR_BTNS.forEach(id => {
        const el = document.getElementById(id);
        if (el) el.disabled = busy;
    });
    const s = document.getElementById('memToolbarStatus');
    if (!s) return;
    if (busy) {
        s.innerHTML = '<span class="spinner-border spinner-border-sm me-1 align-middle" role="status" aria-hidden="true"></span>' + (status || '');
    } else {
        s.textContent = status || '';
    }
}

window.importMemories = async function (mode) {
    const msg = mode === 'replace'
        ? 'Load from Rig (Replace all):\n\nThis will replace ALL app memories with channels read from the radio. Your current app memories will be lost.\n\nContinue?'
        : 'Load from Rig (Add new):\n\nThis will add radio channels to your existing app memories. Nothing will be deleted.\n\nContinue?';
    if (!await _memConfirm(msg)) return;
    _setToolbarBusy(true, 'Reading rig — this may take up to 30 s…');
    try {
        const resp = await fetch('/api/memory/import-radio', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ mode })
        });
        if (resp.ok) {
            const data = await resp.json();
            if (data.warning) {
                _setToolbarBusy(false, `⚠ ${data.warning}`);
            } else {
                _setToolbarBusy(false, `✓ Loaded ${data.imported} from rig`);
                await _loadAndRender();
            }
        } else {
            _setToolbarBusy(false, '✗ Load failed — is the radio connected?');
        }
    } catch (e) {
        _setToolbarBusy(false, '✗ Error');
        console.error('Memory import failed:', e);
    }
};

window.exportMemories = async function (mode) {
    if (mode === 'replace') {
        const count = _memories.length;
        const msg = `Save to Rig (Replace all):\n\nThis will write your ${count} app ${count === 1 ? 'memory' : 'memories'} to the radio, replacing ALL existing radio channels.\n\nAny memories stored on the rig that are not in the app will be lost.\n\nContinue?`;
        if (!await _memConfirm(msg)) return;
        _setToolbarBusy(true, 'Writing to rig…');
        try {
            const resp = await fetch('/api/memory/export-radio', { method: 'POST' });
            if (resp.ok) {
                const data = await resp.json();
                _setToolbarBusy(false, `✓ Saved ${data.written} to rig`);
            } else {
                _setToolbarBusy(false, '✗ Save failed — is the radio connected?');
            }
        } catch (e) {
            _setToolbarBusy(false, '✗ Error');
            console.error('Memory export failed:', e);
        }
    } else {
        const count = _memories.length;
        const msg = `Save to Rig (Add new):\n\nThis will write your ${count} app ${count === 1 ? 'memory' : 'memories'} into empty channels on the rig.\n\nExisting rig channels will not be touched.\n\nContinue?`;
        if (!await _memConfirm(msg)) return;
        _setToolbarBusy(true, 'Scanning rig for empty channels…');
        try {
            const resp = await fetch('/api/memory/export-radio-add', { method: 'POST' });
            if (resp.ok) {
                const data = await resp.json();
                if (data.written === 0 && data.noRoom > 0) {
                    _setToolbarBusy(false,
                        `Rig is full — no empty channels available. Use "Save to Rig (Replace all)" or free up some channels first.`);
                } else if (data.noRoom > 0) {
                    _setToolbarBusy(false,
                        `✓ Saved ${data.written} to rig. ${data.noRoom} couldn't fit — rig is full.`);
                } else {
                    _setToolbarBusy(false, `✓ Saved ${data.written} to rig`);
                }
            } else {
                _setToolbarBusy(false, '✗ Save failed — is the radio connected?');
            }
        } catch (e) {
            _setToolbarBusy(false, '✗ Error');
            console.error('Memory export-add failed:', e);
        }
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
