// Composer keyboard shim: prevent the textarea from inserting a newline on
// bare Enter (Shift+Enter / Ctrl+Enter still insert one), so the Blazor
// @onkeydown handler can treat Enter as "send" without a stray "\n" landing
// in the message body via the oninput binding.

export function preventBareEnterNewline(textarea) {
    if (!textarea) return;
    textarea.addEventListener('keydown', (e) => {
        if (e.key === 'Enter'
            && !e.shiftKey
            && !e.ctrlKey
            && !e.altKey
            && !e.metaKey) {
            e.preventDefault();
        }
    });
}
