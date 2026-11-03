// Tells the server which spectrum panes this page has on screen. The
// cross-band peek borrows the receiver (and dips the audio) every few seconds
// to fill the watch pane, and the server only runs it while some browser says
// it is showing that pane (RadioHub.SpectrumPanels). Classic reports from
// Index.cshtml; the Radio panel and its spectrum window report through this.
// Without it, a cross-band watch pane on the Radio panel only ever said
// "off-screen" unless a Classic tab happened to be open as well.
//
// IWC-only: the peek exists because the IC-7300 has one receiver.
//
// want() returns { A, B }: the panes the operator has chosen to see. A page
// that is not on screen (another tab in front, minimised) wants neither.
// Checked every second and sent when it changes, and re-sent every 5 s anyway:
// an automatic reconnect gives the connection a new id, and the hub keeps one
// record per id.
export function reportSpectrumPanes(conn, want) {
    let last = '';
    let lastAt = 0;
    const send = () => {
        if (conn.state !== 'Connected') return;
        const w = document.visibilityState === 'visible' ? want() : { A: false, B: false };
        const a = !!w.A, b = !!w.B;
        const key = `${a}${b}`;
        const now = Date.now();
        if (key === last && now - lastAt < 5000) return;
        last = key;
        lastAt = now;
        conn.invoke('SpectrumPanels', a, b).catch(() => { /* reconnecting */ });
    };
    setInterval(send, 1000);
    document.addEventListener('visibilitychange', send);
    return send;
}
