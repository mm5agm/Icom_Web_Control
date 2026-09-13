/*
 * layout-store.js — where a workspace layout is kept.
 * Shared by Icom Web Control and Yaesu Web Control.
 *
 * localStorage, per device, under `<prefix>.layout.<name>`.
 *
 * WHY THE DEVICE AND NOT THE SERVER
 * ---------------------------------
 * A layout is a statement about a SCREEN. The shack's 27-inch monitor and the
 * tablet propped on the bench cannot usefully share one, and a layout synced
 * from the wide screen to the narrow one arrives as a squashed mess the
 * operator then has to repair — on the device least convenient to repair it
 * on. Both applications already keep ten-odd view preferences this way
 * (spectrum span, panel open/closed, waterfall brightness), so this follows
 * the existing pattern rather than inventing one.
 *
 * Which layout is CURRENT is a different question from what it contains, and
 * is stored separately so that switching between saved layouts does not
 * rewrite them.
 *
 * EVERY READ AND WRITE IS WRAPPED
 * -------------------------------
 * Private browsing, blocked site data and a full quota all make localStorage
 * throw rather than return null. A layout is a preference: losing one is an
 * annoyance, and an exception thrown while restoring one on load would take
 * the whole page down with it. So a failure here means "no stored layout" and
 * nothing more, and the caller gets the default.
 */

import { LAYOUT_VERSION } from './layout-model.js';

/** The name used when the operator has not saved any of their own. */
export const DEFAULT_LAYOUT_NAME = 'default';

/**
 * Create a store bound to one application's key prefix ("iwc" / "ywc").
 * The prefix is passed in rather than detected, so that a page can never
 * read the other application's layouts if both are open on one machine.
 */
export function createLayoutStore(prefix, storage) {
    const root = `${prefix || 'rwc'}.layout`;
    // Injectable so the tests can drive it with a Map rather than a browser.
    const store = storage || (typeof localStorage !== 'undefined' ? localStorage : null);

    function get(key) {
        try { return store ? store.getItem(key) : null; } catch { return null; }
    }
    function put(key, value) {
        try {
            if (value === null) { store.removeItem(key); } else { store.setItem(key, value); }
            return true;
        } catch { return false; }
    }

    function parse(raw) {
        if (!raw) { return null; }
        let doc;
        try { doc = JSON.parse(raw); } catch { return null; }
        if (!doc || typeof doc !== 'object' || !Array.isArray(doc.panels)) { return null; }
        // A layout from a future version is not readable and must not be
        // guessed at — returning null loses the operator's arrangement, which
        // is bad, but rendering a misread one loses their radio, which is
        // worse and harder to explain.
        const v = Math.floor(doc.version) || 0;
        if (v > LAYOUT_VERSION) { return null; }
        return migrate(doc, v);
    }

    /**
     * Bring an older stored layout up to the current version.
     * Nothing to do yet — version 1 is the first — but the hook exists now
     * so the first migration is a case in a switch rather than a redesign,
     * and so that the version number is load-bearing from the start rather
     * than decorative.
     */
    function migrate(doc, _fromVersion) {
        return { ...doc, version: LAYOUT_VERSION };
    }

    return {
        prefix: root,

        /** Read one named layout, or null. */
        load(name) {
            return parse(get(`${root}.${name || DEFAULT_LAYOUT_NAME}`));
        },

        /** Write one named layout. Returns false when storage refused it. */
        save(name, layout) {
            const doc = { ...layout, version: LAYOUT_VERSION, savedAt: new Date().toISOString() };
            try {
                return put(`${root}.${name || DEFAULT_LAYOUT_NAME}`, JSON.stringify(doc));
            } catch {
                return false;
            }
        },

        /** Forget one named layout. */
        remove(name) {
            if (!name || name === DEFAULT_LAYOUT_NAME) { return false; }
            return put(`${root}.${name}`, null);
        },

        /** The names of every saved layout, sorted. */
        names() {
            const out = [];
            try {
                for (let i = 0; i < store.length; i++) {
                    const k = store.key(i);
                    if (k && k.startsWith(`${root}.`) && k !== `${root}.current`) {
                        out.push(k.slice(root.length + 1));
                    }
                }
            } catch { /* unreadable storage -> no saved layouts */ }
            return out.sort();
        },

        /** Which layout this device is showing. */
        currentName() {
            return get(`${root}.current`) || DEFAULT_LAYOUT_NAME;
        },

        setCurrentName(name) {
            return put(`${root}.current`, name || DEFAULT_LAYOUT_NAME);
        }
    };
}
