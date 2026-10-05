// Service Worker for PoPunkouterSoftware PWA
// Provides offline read-through caching for application shells and telemetry snapshots.

const CACHE_NAME = 'pops-pwa-v1';
const PRECACHE_URLS = [
    '/',
    '/azure',
    '/users',
    '/settings',
    '/css/boot.css',
    '/css/modern-ui.css',
    '/js/app-boot.js',
    '/js/theme-kit.js',
    '/js/motion-kit.js',
    '/js/audio-kit.js',
    '/js/helpers.js',
    '/js/command-palette.js',
    '/js/numeric-ticker.js',
    '/images/favicon.ico'
];

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE_NAME).then((cache) => {
            return cache.addAll(PRECACHE_URLS);
        }).then(() => self.skipWaiting())
    );
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys().then((keys) => {
            return Promise.all(
                keys.filter((key) => key !== CACHE_NAME).map((key) => caches.delete(key))
            );
        }).then(() => self.clients.claim())
    );
});

self.addEventListener('fetch', (event) => {
    if (event.request.method !== 'GET') return;

    // API endpoints: Network-first, fallback to cache for offline availability
    if (event.request.url.includes('/api/')) {
        event.respondWith(
            fetch(event.request)
                .then((response) => {
                    if (response && response.status === 200) {
                        const copy = response.clone();
                        caches.open(CACHE_NAME).then((cache) => cache.put(event.request, copy));
                    }
                    return response;
                })
                .catch(() => caches.match(event.request))
        );
        return;
    }

    // Static assets & Pages: Cache-first, fallback to network
    event.respondWith(
        caches.match(event.request).then((cached) => {
            return cached || fetch(event.request).then((networkResponse) => {
                if (networkResponse && networkResponse.status === 200 && !event.request.url.startsWith('chrome-extension')) {
                    const copy = networkResponse.clone();
                    caches.open(CACHE_NAME).then((cache) => cache.put(event.request, copy));
                }
                return networkResponse;
            });
        }).catch(() => {
            if (event.request.mode === 'navigate') {
                return caches.match('/');
            }
        })
    );
});
