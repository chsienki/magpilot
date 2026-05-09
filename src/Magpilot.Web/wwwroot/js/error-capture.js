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

window.magpilot.scrollToBottom = window.magpilot.scrollToBottom || function (el) {
    if (el && typeof el.scrollTo === 'function') {
        el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
    } else if (el) {
        el.scrollTop = el.scrollHeight;
    }
};
