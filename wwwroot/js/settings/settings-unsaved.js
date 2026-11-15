// Settings: say so when there are changes that haven't been saved.
//
// A tester changed the band plan region and the main page still showed the
// old one, most likely because the change was never saved (IWC #137). So once
// anything in #settingsForm differs from what the page loaded with, a bar at
// the bottom says the changes aren't saved yet, with its own Save button, and
// leaving the page asks first. Changing a setting back clears it.
//
// The page's own scripts fill some fields after load (port lists and the
// like), so the starting point is taken a moment after load, and the form is
// only compared once the user has touched it.
(function () {
    'use strict';
    var form = document.getElementById('settingsForm');
    if (!form) return;

    var baseline = null;
    var touched = false;
    var dirty = false;
    var submitting = false;
    var bar = null;

    function snapshot() {
        var parts = [];
        new FormData(form).forEach(function (value, name) {
            if (name === '__RequestVerificationToken') return;
            parts.push(name + '=' + (typeof value === 'string' ? value : (value && value.name) || ''));
        });
        return parts.join('&');
    }

    function makeBar() {
        var div = document.createElement('div');
        div.id = 'settingsUnsavedBar';
        div.className = 'alert alert-warning shadow d-flex align-items-center gap-3 mb-0 py-2';
        div.setAttribute('role', 'status');
        div.style.cssText = 'position:fixed;left:50%;bottom:1rem;transform:translateX(-50%);z-index:1080;max-width:95vw;';
        div.hidden = true;
        div.innerHTML =
            '<i class="bi bi-exclamation-triangle-fill" aria-hidden="true"></i>' +
            '<span>Your changes are not saved yet. They take effect when you press Save.</span>' +
            '<button type="submit" class="btn btn-primary btn-sm text-nowrap"><i class="bi bi-save"></i> Save Settings</button>';
        form.appendChild(div);
        return div;
    }

    function check() {
        if (baseline === null || !touched) return;
        var now = snapshot() !== baseline;
        if (now === dirty) return;
        dirty = now;
        if (!bar) bar = makeBar();
        bar.hidden = !dirty;
    }

    function onEdit() {
        touched = true;
        check();
    }

    form.addEventListener('input', onEdit);
    form.addEventListener('change', onEdit);
    // Some editors keep their value in a hidden field set by script, which
    // fires no event: look again shortly after any click in the form.
    form.addEventListener('click', function () { touched = true; setTimeout(check, 250); });
    setInterval(check, 1500);

    form.addEventListener('submit', function (e) {
        submitting = true;
        // Client-side validation can stop the post: then it's still unsaved.
        setTimeout(function () { if (e.defaultPrevented) submitting = false; }, 0);
    });

    window.addEventListener('beforeunload', function (e) {
        if (!dirty || submitting) return;
        e.preventDefault();
        e.returnValue = '';
    });

    function start() { setTimeout(function () { baseline = snapshot(); check(); }, 1000); }
    if (document.readyState === 'complete') start();
    else window.addEventListener('load', start);

    window.SettingsUnsaved = {
        isDirty: function () { return dirty; },
        ready: function () { return baseline !== null; },
    };
})();
