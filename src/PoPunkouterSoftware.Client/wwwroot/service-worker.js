// Retired 2026-10-19. The app has no offline mode; this worker used to serve every page and
// script cache-first under a hand-bumped cache name, so a visitor saw the PREVIOUS deploy until
// someone remembered to bump it. Nothing registers it any more (js/app-boot.js), but browsers
// that installed it keep it until this URL serves a replacement — so this file stays, deletes
// every cache it made, and unregisters itself. Having no fetch handler, it never intercepts a
// request in the meantime. Delete the file once old installs have had time to age out.
self.addEventListener('install', () => self.skipWaiting());

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        for (const key of await caches.keys()) await caches.delete(key);
        await self.registration.unregister();
    })());
});
