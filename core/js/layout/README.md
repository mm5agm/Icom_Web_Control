# The workspace layout engine

A dockable-panel layout: the operator arranges the panels their station
actually uses, and the arrangement is theirs and persists. Shared by Icom Web
Control and Yaesu Web Control, because none of it knows what a radio is.

The four modules are deliberately separate, in a line from "no browser at all"
to "nothing but browser":

| module | knows about | testable |
|---|---|---|
| `layout-grid.js` | rectangles on a column grid | yes, purely |
| `layout-model.js` | which panels exist and where they sit | yes, purely |
| `layout-store.js` | `localStorage` keys and the on-disk shape | yes, with a fake storage |
| `workspace.js` | the DOM, pointers, keyboard | no — verified in a browser |

That split is the point. Everything that can be got wrong silently — two panels
overlapping, a panel pushed off the grid, a saved layout read back wrong, a
panel that vanishes when an SDR is unplugged — lives in the three modules with
tests. `workspace.js` moves elements and listens for events, and holds no
geometry of its own.

## `layout-grid.js` — geometry

Pure functions over `{col, row, w, h}` rectangles on a grid of `columns`
columns and unbounded rows. `resolveCollisions`, `compact`, `movePanel`,
`resizePanel`, `reflow`, `findSlot`.

Two decisions worth knowing before changing anything here:

- **Displacement is downward only.** A panel that is in the way moves down,
  never sideways. Horizontal position is muscle memory — the band buttons are
  on the left of the receiver panel — and a sideways shuffle rearranges panels
  the operator never touched.
- **`normaliseRect` clamps width before column**, not the other way round. The
  other order lets a wide panel at a high column silently shrink instead of
  sliding left, and the operator sees a panel that quietly got smaller with no
  cause they can point at.

## `layout-model.js` — what a layout is

A **catalogue** is what this station can show: `{id, title, group, w, h, minW,
minH, essential, available}` per panel. Each application builds its own from
its own capabilities, so a catalogue is *not* shareable. A **layout** is where
those panels sit, and is.

- `reconcile(stored, catalogue, columns)` is the function that matters. A
  stored layout meets a catalogue that has changed since — an SDR was
  unplugged, the app gained a panel, the operator swapped radio model. It drops
  what is gone, appends what is genuinely new, and leaves the arrangement
  otherwise alone. A panel the operator deliberately hid stays hidden across
  that round trip; resurrecting it on every unplug/replug would be the most
  irritating possible bug.
- **`essential` panels cannot be hidden.** A workspace that can hide anything
  can hide the operator's way back to the radio, and the layout survives a
  reload, so the bad state is not one refresh away from cured. Enforced here,
  not in the UI: `workspace.js` simply renders no close button.
- `applyPreset` names panels and relative sizes, never absolute positions, so
  the same preset is sensible at 12, 6 and 1 columns.

## `layout-store.js` — persistence

`createLayoutStore(prefix, storage)` over `localStorage`, keyed
`<prefix>.layout.<name>` with `<prefix>.layout.current` alongside. The prefix
is per-application, so the two apps cannot read each other's layouts.

- **Per device, not per user on the server.** A layout is a statement about a
  *screen*. The shack's 27-inch monitor and the tablet on the bench cannot
  share one, and syncing them would make each worse.
- **A layout from a future version is refused**, returning null rather than
  something half-understood. Losing an arrangement is annoying; rendering a
  misread one loses the radio. `migrate(doc, fromVersion)` is a no-op hook so
  the first real migration is a switch case rather than a redesign.
- Every read and write is in a try/catch. `localStorage` throws outright in a
  private window, and the page must still render the default arrangement.

## `workspace.js` — the DOM

`createWorkspace({container, rail, catalogue, prefix, presets, ...})`.

**It moves the application's own live elements; it never rebuilds markup.**
Both applications find every control by `id` — IWC alone has some five hundred
`getElementById` calls — and never by container, so `appendChild` on a live
node keeps every value, every handler, every SignalR update and every aria
attribute working. Cloning or re-rendering the markup would break all of that
silently, and only against a radio.

- The application marks its own sections with `data-panel="<id>"`. The engine
  looks for that attribute and nothing else; it never guesses at structure.
- Each moved element leaves a **comment marker** behind (`<!-- panel:id -->`),
  so switching back to Classic puts it back exactly where it was even if its
  siblings have changed since.
- `render()` appends in the layout's reading order, so the tab sequence matches
  what is on the screen. A panel whose markup is not on this page is dropped
  rather than rendered as an empty box.
- **Keyboard move and resize are not optional.** Arrows move from the move
  handle, Shift+arrows resize. Both applications have operators driving them by
  head-tracking and an on-screen keyboard; a drag-only workspace takes a
  rearrangeable layout away from exactly the people it helps most.
- A narrow window **reflows but does not save**. Visiting on a phone must not
  rewrite the arrangement built for the desktop.
- Breakpoints are three fixed column counts — 12 / 6 / 1 — not a continuum. A
  layout that reflows on every pixel of window width is one the operator cannot
  save.

The chrome for all of this is `core/css/layout-workspace.css`, scoped entirely
to `[data-layout="workspace"]`.

## Tests

Node's built-in runner, no npm dependencies. From the core repo root:

```
node --test "tests/js/layout-grid.test.mjs" "tests/js/layout-model.test.mjs"
```

Quote the paths: the bare directory form fails on Node 24 under Windows.

Both suites include property tests — a fixed seed, a few hundred random moves
and resizes, asserting after every one that nothing overlaps, nothing is lost,
nothing is off the grid and nothing has collapsed to zero. The seed is fixed so
a failure is reproducible rather than a story about something that happened
once.
