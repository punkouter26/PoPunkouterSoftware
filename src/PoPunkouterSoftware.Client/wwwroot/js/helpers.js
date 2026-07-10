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
 * One-tap copy clipboard helper with a micro-notification overlay
 */
window.copyToClipboard = function (text) {
    navigator.clipboard.writeText(text).then(() => {
        const toast = document.createElement('div');
        toast.className = 'app-micro-toast';
        toast.innerText = 'Copied suggested fix!';
        document.body.appendChild(toast);
        setTimeout(() => toast.classList.add('show'), 20);
        setTimeout(() => {
            toast.classList.remove('show');
            setTimeout(() => {
                if (toast.parentNode) {
                    document.body.removeChild(toast);
                }
            }, 300);
        }, 1800);
    }).catch(err => {
        console.error('Clipboard copy failed: ', err);
    });
};
