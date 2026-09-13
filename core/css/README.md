# Shared stylesheets

The same argument as `core/js/`: this is a class library *and* a folder of
browser assets, because roughly half the duplication between IWC and YWC is
in the browser rather than in C#.

## What's here

- `layout-workspace.css` — chrome for the dockable-panel workspace: the panel
  shell, its header, the move handle, the resize grip and the rail. It styles
  the container the engine builds and nothing inside a panel, which is what
  makes it shareable — the two applications' panels have nothing in common,
  but a panel *frame* is a panel frame. Paired with `core/js/layout/`.

  Every rule is scoped to `[data-layout="workspace"]` on `<html>`. Without
  that attribute not one selector in the file matches, so adding it cannot
  change the Classic page — that is not "believed unchanged", it is
  unreachable. This is the first thing to check if a review suspects
  otherwise.

  Colours are read from theme tokens (`--rwc-panel`, `--rwc-border`,
  `--rwc-focus-ring`, `--rwc-target-min` and friends) with the literal
  Bootstrap 5.3 defaults as fallbacks, so the sheet works whether or not the
  consuming application has adopted themes.

Each application keeps its own local sheet for the components named after
its own markup. Same split as the calibration engine and its tables: core
knows the shape, the app knows the specifics.

## Why these are copied, not compiled

`RadioWebControl.Core.csproj` excludes `css/**` from MSBuild, exactly as it
excludes `js/**`. These are served to a browser, so each application copies
the ones it uses into its own `wwwroot/css/` at build time.

**A wrong path here fails silently in a browser**, where a wrong C#
namespace fails loudly at compile time — the same hazard `core/js/README.md`
describes, and the reason both apps' targets emit a `.gitignore` naming
what they generated rather than leaving anyone to hand-maintain one.

## How each app consumes these (the copy)

Each app's `.csproj` has a `CopySharedCoreCss` target that copies
`core/css/**/*.css` into its own `wwwroot/css/` early in the build, before
ASP.NET resolves static web assets. It mirrors `CopySharedCoreJs` line for
line, including adding the freshly-copied files to `@(Content)` itself —
the default `wwwroot/**` glob is evaluated *before* the copy runs, so on a
clean checkout the files do not exist yet when the glob is computed, and
without that step the very first build would 404 them.

The copies are **generated output**. Edit the file here; never edit the
`wwwroot` copy, because the next build overwrites it.

## Testing

There is nothing to unit-test in a stylesheet, and both applications verify
UI by hand in a browser. What is worth checking on any change here, because
it is the part that fails silently:

1. Switch the application back to its Classic layout and confirm the page is
   byte-for-byte the page it was — the scoping argument above is only as good
   as the attribute actually being absent.
2. Tab right through a workspace and confirm the focus ring is visible on
   every move handle and close button, and that every panel can be moved and
   resized from the keyboard alone. An operator using head-tracking walks the
   tab sequence; a workspace reachable only by dragging takes the layout away
   from the people a rearrangeable layout helps most.
3. Load with `localStorage` blocked (a private window) — the stored layout is
   unreadable there, so the page must fall back to the default arrangement
   and still render.
