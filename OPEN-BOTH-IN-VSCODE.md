# Opening YWC and IWC together in VS Code

The two projects are separate folders on disk:

- **YWC:** `c:\Users\colin\source\repos\Yaesu_Web_Control`
- **IWC:** `c:\Users\colin\source\repos\Icom_Web_Control`

There are two ways to have both open at once. Use **Option 1 for actual development** — it keeps each Claude Code session cleanly scoped to one repo.

---

## Option 1 — Two separate windows (recommended for dev work)

1. Open the first repo: **File → Open Folder…** → pick `Yaesu_Web_Control`.
2. Open a second window: **File → New Window** (`Ctrl+Shift+N`).
3. In that new window: **File → Open Folder…** → pick `Icom_Web_Control`.

You now have two independent VS Code windows side by side. **Each window runs its own Claude Code session, scoped to that one repo** — so Claude in the YWC window only ever touches YWC, and Claude in the IWC window only ever touches IWC. Git commits/pushes from each go to the correct remote automatically.

> Tip: drag one window to each half of the screen (Windows key + ←/→) to see them side by side.

---

## Option 2 — One window, multi-root workspace (handy for browsing both)

This shows both repos as top-level folders in a single Explorer, in one window.

1. Open either repo (**File → Open Folder…**).
2. **File → Add Folder to Workspace…** → add the other repo.
3. **File → Save Workspace As…** → save it (e.g. `Ham-Web-Control.code-workspace`).
4. Reopen anytime via **File → Open Recent**, or by double-clicking the `.code-workspace` file.

A ready-made workspace file already exists at:

```
c:\Users\colin\source\repos\Ham-Web-Control.code-workspace
```

Double-click it to open both repos in one window.

> ⚠️ **Caveat for Claude Code:** in a single multi-root workspace there is one Claude session for the whole window, and its working directory is the primary (first) folder. That makes it easy to accidentally run a command against the wrong repo. Multi-root is great for *reading* both side by side, but when you want Claude to *change* code, prefer **Option 1** so the session is unambiguously scoped to one repo.

---

*This note is kept in both repos. In YWC it's a local-only file (git-ignored) so YWC's public repo stays Icom-free; in IWC it's committed.*
