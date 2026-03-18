import AppSorter from './features/app-sorter.js';
import AppCardRenderer from './features/app-card-renderer.js';

document.addEventListener('DOMContentLoaded', async () => {
    // Track page view if appInsights is available
    if (window.appInsights && typeof window.appInsights.trackPageView === 'function') {
        window.appInsights.trackPageView();
    }

    const appsGrid = document.querySelector('.apps-grid');
    if (appsGrid) {
        const renderer = new AppCardRenderer(appsGrid);
        const apps = await renderer.loadApps();

        if (apps) {
            const sortSelect = document.getElementById('sort-select');
            if (sortSelect) {
                new AppSorter(appsGrid, sortSelect);
            }
        }
    }
});


