# Using Claude Code (not GitHub Copilot) in a VS Code window

If a chat panel shows **"No models available"** or **"Subscribe to GitHub Copilot Pro"**, that panel is **GitHub Copilot** — a different product from Claude Code. It has nothing to do with your Claude subscription. You just need to use the Claude Code panel instead, and (optionally) silence or remove Copilot.

**There is nothing to "activate" per project.** Claude Code signs in once at the machine level, so once you're logged in it works in *every* VS Code window automatically — no per-folder subscription step.

---

## Open Claude Code

- Click the **Claude icon in the Activity Bar** (the icon strip on the far left) — the same one you use in the other window. It opens already signed in.
- If the icon isn't visible: **Command Palette** (`Ctrl+Shift+P`) → type **"Claude"** → choose the command to open/focus Claude Code.

If Claude Code doesn't appear at all, it may be disabled for this workspace:
1. **Extensions** view (`Ctrl+Shift+X`) → search **"Claude"**.
2. If it shows **Enable** or **Enable (Workspace)**, click it (make sure it's not *Disable (Workspace)*).
3. Reload the window if prompted.

---

## Silence GitHub Copilot — two options

Open the **Extensions** view (`Ctrl+Shift+X`) and search **"Copilot"**. You'll see **GitHub Copilot** and **GitHub Copilot Chat**. For each one, click the **gear ⚙ icon** next to it:

### Option A — Disable (keeps it installed, stops the prompts)
- Choose **Disable** (everywhere) or **Disable (Workspace)** (just this repo).
- Reversible anytime via the same menu → **Enable**.
- Recommended if you might want Copilot back later.

### Option B — Uninstall (removes it completely)
- Choose **Uninstall**.
- The extension is removed entirely; the "Subscribe to Copilot Pro" prompt can't come back.
- You can always reinstall later from the Extensions Marketplace if you change your mind.

Do this for **both** "GitHub Copilot" and "GitHub Copilot Chat" so no Copilot prompt remains.

---

## After changing extensions

Reload the window so the change takes effect: **Command Palette** (`Ctrl+Shift+P`) → **Developer: Reload Window**, or simply close the VS Code window and reopen the folder. Then open the Claude Code panel as above.

---

*This note is kept in both repos. In YWC it's a local-only file (git-ignored); in IWC it's committed.*
