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

window.poAppStorage = {
    get: function (key) {
        return window.localStorage.getItem(key);
    },
    set: function (key, value) {
        window.localStorage.setItem(key, value);
    },
    remove: function (key) {
        window.localStorage.removeItem(key);
    }
};
