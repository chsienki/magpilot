// Chat-history infinite-scroll helpers. Used by ChatView to demand-load
// older messages when the user scrolls near the top of a long chat, and
// to preserve scroll position when those older messages are prepended.

const TRIGGER_PX = 120; // fire load-more when within this many px of the top

/**
 * Install a scroll watcher on `el` that calls `dotNetRef.invokeMethodAsync('OnNearTop')`
 * whenever the user scrolls within TRIGGER_PX of the top. Throttled so a
 * fast scroll only fires once per "near-top" episode (re-arms when the
 * user scrolls back down past the trigger zone).
 *
 * Returns a disposer the caller should invoke on component dispose.
 */
export function watchScrollTop(el, dotNetRef) {
    if (!el || !dotNetRef) return null;
    let armed = true;
    const onScroll = () => {
        if (!armed) {
            if (el.scrollTop > TRIGGER_PX * 2) armed = true;
            return;
        }
        if (el.scrollTop <= TRIGGER_PX) {
            armed = false;
            dotNetRef.invokeMethodAsync('OnNearTop').catch(() => { /* component disposed */ });
        }
    };
    el.addEventListener('scroll', onScroll, { passive: true });
    return {
        dispose: () => el.removeEventListener('scroll', onScroll),
    };
}

/**
 * Snapshot the current scroll position. Returns { scrollTop, scrollHeight }.
 * Pair with restoreScrollAfterPrepend to keep the user's visual anchor
 * stable when older messages are prepended above the current view.
 */
export function snapshotScroll(el) {
    if (!el) return null;
    return { scrollTop: el.scrollTop, scrollHeight: el.scrollHeight };
}

/**
 * After prepending content above the current scroll position, restore the
 * visual anchor by shifting scrollTop by the height delta.
 */
export function restoreScrollAfterPrepend(el, snapshot) {
    if (!el || !snapshot) return;
    const delta = el.scrollHeight - snapshot.scrollHeight;
    el.scrollTop = snapshot.scrollTop + delta;
}
