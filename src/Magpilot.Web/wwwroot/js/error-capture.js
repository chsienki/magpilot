// Browser-side error capture. Wires window.onerror + unhandledrejection to
// a .NET-side callback so they end up in the central log via HubLogClient.
//
// The callback is a [JSInvokable] static method on
// Magpilot.UI.Services.JsErrorBridge so we don't have to keep a per-component
// DotNetObjectReference alive (these errors come from anywhere).

window.magpilot = window.magpilot || {};

window.magpilot.installErrorCapture = function () {
    if (window.magpilot._errorCaptureInstalled) return;
    window.magpilot._errorCaptureInstalled = true;

    // 1. Synchronous JS errors. The 'message' event arg is a plain string for
    //    cross-origin scripts; for same-origin we get a richer ErrorEvent.
    window.addEventListener('error', function (e) {
        const msg = (e && e.message) ? e.message : 'window.error (no message)';
        const stack = e && e.error && e.error.stack ? String(e.error.stack)
                    : (e && e.filename ? `${e.filename}:${e.lineno}:${e.colno}` : null);
        invoke('window.error', msg, stack);
    });

    // 2. Unhandled promise rejections. Reason can be anything; coerce to string.
    window.addEventListener('unhandledrejection', function (e) {
        const reason = e && e.reason;
        let msg = 'unhandledrejection';
        let stack = null;
        if (reason instanceof Error) {
            msg = reason.message || msg;
            stack = reason.stack || null;
        } else if (typeof reason === 'string') {
            msg = reason;
        } else {
            try { msg = JSON.stringify(reason); } catch { msg = String(reason); }
        }
        invoke('unhandledrejection', msg, stack);
    });

    function invoke(kind, message, stack) {
        try {
            DotNet.invokeMethodAsync('Magpilot.UI', 'CaptureJsError', kind, message, stack)
                .catch(function () { /* swallow - logging-of-logging spiral defence */ });
        } catch {
            /* DotNet not ready yet - drop. */
        }
    }
};

// --------------------------------------------------------------------
// Yellow-bar capture (runs early, before WASM has even started).
//
// Blazor WASM shows #blazor-error-ui ("An unhandled error has occurred.
// Reload.") whenever its `vt` handler fires -- which happens both for
// genuine unhandled .NET exceptions AND for any C# `Console.Error`
// write (the runtime config wires stderr through the same
// dotNetCriticalError handler). Magpilot code uses `Console.WriteLine`
// (not `Console.Error.WriteLine`) for recovery-path diagnostics so it
// doesn't accidentally trigger the banner; this shim is the
// belt-and-braces backstop for cases we missed or that come from
// upstream libraries.
//
// On show, the shim:
//   1. Injects a scrollable <pre> with the most-recently-captured
//      console.error / console.warn / window.error /
//      unhandledrejection text into the banner so the user can read
//      what crashed without leaving the page (devastating on mobile
//      where there's no F12 console).
//   2. POSTs the same text to /api/log via navigator.sendBeacon --
//      this is the only HTTP path that reliably ships when the WASM
//      runtime is poisoned or the page is unloading, because the
//      browser owns it on a background thread.
//
// The IIFE runs at script-tag-load time (NOT inside
// installErrorCapture) so a crash during WASM bootstrap is captured
// too -- installErrorCapture only fires after Blazor has mounted
// MainLayout and called us from OnAfterRenderAsync.
(function () {
    // Bump SHIM_VERSION on every behavioural change here. The banner
    // and the sendBeacon payload both surface it so we can tell at a
    // glance whether the browser fetched the new bundle vs a cached
    // older one -- wwwroot/js files have no content-hashed name, so
    // cache invalidation depends entirely on the ?v=N in index.html.
    // Keep this string in sync with that query param.
    const SHIM_VERSION = 'v1';

    if (window.magpilot._fatalCaptureInstalled) return;
    window.magpilot._fatalCaptureInstalled = true;
    window.magpilot._fatalCaptureVersion = SHIM_VERSION;

    const errorBuffer = [];
    const MAX_BUFFER = 10;

    // Defensive filter against re-introducing the `Console.Error`-as-
    // fatal-trigger pitfall: any future code that uses
    // `Console.Error.WriteLine("[HubLogClient] ...")` (the canonical
    // recovery-path noise) wouldn't show in the banner because the
    // filter pulls it from the buffer before the user sees it. Add
    // patterns here as we discover more known-recoverable noise.
    const NOISE_PATTERNS = [
        /^\[HubLogClient\] /,
    ];

    function snapshot(args) {
        const parts = [];
        for (let i = 0; i < args.length; i++) {
            const a = args[i];
            if (a instanceof Error) {
                parts.push(a.stack || a.message || String(a));
            } else if (typeof a === 'object' && a !== null) {
                try { parts.push(JSON.stringify(a)); }
                catch { parts.push(String(a)); }
            } else {
                parts.push(String(a));
            }
        }
        return parts.join(' ');
    }

    function push(level, text) {
        if (!text) return;
        for (let i = 0; i < NOISE_PATTERNS.length; i++) {
            if (NOISE_PATTERNS[i].test(text)) return;
        }
        errorBuffer.push({ ts: Date.now(), level: level, text: text });
        if (errorBuffer.length > MAX_BUFFER) errorBuffer.shift();
    }

    const origErr = console.error;
    console.error = function () {
        try { push('error', snapshot(arguments)); }
        catch { /* don't break console.error */ }
        return origErr.apply(console, arguments);
    };

    const origWarn = console.warn;
    console.warn = function () {
        try { push('warn', snapshot(arguments)); }
        catch { /* ignore */ }
        return origWarn.apply(console, arguments);
    };

    // window-level fallbacks for failure modes that surface from the
    // JS glue (e.g. a rejected Blazor.start() promise) rather than
    // through .NET-side console.error. Both fire independently of the
    // .NET-side JsErrorBridge capture (which dies with the dispatcher).
    window.addEventListener('error', function (e) {
        try {
            const msg = (e && e.message) ? e.message : 'window.error (no message)';
            const stack = e && e.error && e.error.stack ? String(e.error.stack)
                        : (e && e.filename ? `${e.filename}:${e.lineno}:${e.colno}` : null);
            push('error', '[window.error] ' + msg + (stack ? '\n' + stack : ''));
        } catch { /* ignore */ }
    });
    window.addEventListener('unhandledrejection', function (e) {
        try {
            const reason = e && e.reason;
            let msg = 'unhandledrejection';
            let stack = null;
            if (reason instanceof Error) {
                msg = reason.message || msg;
                stack = reason.stack || null;
            } else if (typeof reason === 'string') {
                msg = reason;
            } else {
                try { msg = JSON.stringify(reason); } catch { msg = String(reason); }
            }
            push('error', '[unhandledrejection] ' + msg + (stack ? '\n' + stack : ''));
        } catch { /* ignore */ }
    });

    function attachObserver() {
        const ui = document.getElementById('blazor-error-ui');
        if (!ui) {
            // index.html parses top-to-bottom; this script runs before
            // the body finishes -- retry once DOM is ready.
            document.addEventListener('DOMContentLoaded', attachObserver, { once: true });
            return;
        }
        let injected = false;
        const observer = new MutationObserver(function () {
            const visible = ui.style.display !== '' && ui.style.display !== 'none';
            if (visible && !injected) {
                injected = true;
                onErrorUiShown(ui);
            }
        });
        observer.observe(ui, { attributes: true, attributeFilter: ['style'] });
    }
    attachObserver();

    // One-shot Info-level row to /api/log at script-load time so we
    // can verify from a different device that a phone fetched the
    // latest bundle without having to crash + look at the banner.
    // Best-effort; sendBeacon silently drops if the user isn't
    // authenticated yet.
    try {
        const ping = {
            source:    'spa-bootstrap',
            level:     'Information',
            category:  'fatal-capture',
            message:   'fatal-capture shim ' + SHIM_VERSION + ' installed on ' + location.href,
            url:       location.href,
            userAgent: navigator.userAgent
        };
        const body = new Blob([JSON.stringify(ping)], { type: 'application/json' });
        navigator.sendBeacon('/api/log', body);
    } catch { /* best-effort */ }

    function onErrorUiShown(ui) {
        const errors = errorBuffer.filter(e => e.level === 'error');
        const chosen = errors.length ? errors : errorBuffer;
        const entries = chosen.slice(0, 5);
        const blob = entries.length
            ? entries.map(function (e, i) {
                return '#' + (i + 1) + ' [' + e.level + '] (' + new Date(e.ts).toISOString() + ')\n' + e.text;
              }).join('\n\n---\n\n')
            : '(no console error captured before the banner showed -- check /admin/logs for framework errors)';

        // Inject the version chip + scrollable details into the banner.
        // The leading text node ("An unhandled error has occurred.")
        // stays as-is; the version chip sits inline next to it; the
        // details land underneath. The existing Reload link and
        // dismiss button stay clickable at their original positions.
        try {
            const tag = document.createElement('span');
            tag.className = 'magpilot-fatal-version';
            tag.textContent = ' [shim ' + SHIM_VERSION + ']';
            ui.insertBefore(tag, ui.firstChild ? ui.firstChild.nextSibling : null);

            const pre = document.createElement('pre');
            pre.className = 'magpilot-fatal-details';
            pre.textContent = blob;
            ui.insertBefore(pre, tag.nextSibling);
        } catch { /* DOM mutation failed; sendBeacon below is the fallback */ }

        // Best-effort POST. sendBeacon goes through the browser on a
        // background thread, so it lands even if the WASM dispatcher
        // is dead or the page is mid-unload.
        try {
            const payload = {
                source:    'spa-fatal',
                level:     'Critical',
                category:  'blazor-error-ui',
                message:   '[' + SHIM_VERSION + '] Blazor unhandled error UI shown on ' + location.href,
                stack:     blob,
                url:       location.href,
                userAgent: navigator.userAgent
            };
            const body = new Blob([JSON.stringify(payload)], { type: 'application/json' });
            navigator.sendBeacon('/api/log', body);
        } catch { /* best-effort */ }
    }
})();

window.magpilot.scrollToBottom = window.magpilot.scrollToBottom || function (el) {
    if (el && typeof el.scrollTo === 'function') {
        el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
    } else if (el) {
        el.scrollTop = el.scrollHeight;
    }
};
