/**
 * Trigger a browser file download with the given text content.
 * @param {string} filename  - suggested filename (e.g. "azure-report-2026-05-14.json")
 * @param {string} content   - file content (JSON string)
 * @param {string} mimeType  - MIME type (default: application/json)
 */
window.downloadTextFile = function (filename, content, mimeType) {
    mimeType = mimeType || 'application/json';
    const blob = new Blob([content], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};

/**
 * Track page scroll state to float/hide navbar navigation bar.
 */
window.initNavbarScroll = function () {
    const header = document.querySelector('.rz-header');
    if (!header) return;
    if (window.navbarScrollHeader === header) return;

    if (window.navbarScrollHandler) {
        window.removeEventListener('scroll', window.navbarScrollHandler);
    }

    window.navbarScrollHeader = header;
    let lastScrollY = window.scrollY;
    
    window.navbarScrollHandler = () => {
        if (window.navbarScrollHeader !== header) return;
        const currentScrollY = window.scrollY;
        if (currentScrollY > lastScrollY && currentScrollY > 80) {
            header.classList.add('nav-hidden');
        } else {
            header.classList.remove('nav-hidden');
        }
        if (currentScrollY > 20) {
            header.classList.add('nav-scrolled');
        } else {
            header.classList.remove('nav-scrolled');
        }
        lastScrollY = currentScrollY;
    };
    window.addEventListener('scroll', window.navbarScrollHandler);
};

/**
 * One-tap copy clipboard helper. Returns a promise resolving to true/false so the
 * caller (C#) can show honest feedback — the previous version swallowed failures
 * (the UI said "Copied" with nothing on the clipboard) and threw synchronously on
 * non-secure origins where navigator.clipboard is undefined, which crashed the page
 * through the Blazor error boundary. The single toast is now owned by the C# side.
 */
window.copyToClipboard = function (text) {
    if (!navigator.clipboard) {
        console.error('Clipboard API unavailable (non-secure context?)');
        return Promise.resolve(false);
    }
    return navigator.clipboard.writeText(text)
        .then(() => true)
        .catch(err => {
            console.error('Clipboard copy failed: ', err);
            return false;
        });
};
