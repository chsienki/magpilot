// Tab visibility watcher. Used by Home.razor to refresh the session
// list (and so the per-session title in the AppBar) whenever the user
// switches back to the SPA tab from elsewhere -- typically after they
// ran a `/rename` (or any session-mutating command) in the terminal
// CLI. Without this, the SPA would keep showing the stale session
// name until the user manually clicked the refresh button or did a
// full page reload.
//
// We listen for `visibilitychange` (not `focus`) because mobile
// browsers and tab restoration paths fire the former more reliably
// than the latter.

/**
 * Install a visibility watcher that invokes
 * `dotNetRef.OnTabVisible()` every time the document transitions
 * from hidden to visible. Returns a disposer the caller should
 * invoke on component dispose.
 */
export function watch(dotNetRef) {
    if (!dotNetRef) return null;
    const onChange = () => {
        if (document.visibilityState === 'visible') {
            dotNetRef.invokeMethodAsync('OnTabVisible')
                .catch(() => { /* component disposed; swallow */ });
        }
    };
    document.addEventListener('visibilitychange', onChange);
    return {
        dispose: () => document.removeEventListener('visibilitychange', onChange),
    };
}
