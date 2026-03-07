import './components/site-components.js';
import Analytics from './core/analytics.js';
import AppSorter from './features/app-sorter.js';
import AppCardRenderer from './features/app-card-renderer.js';
import { SELECTORS } from './constants/app-constants.js';

document.addEventListener('DOMContentLoaded', async () => {
    const analytics = new Analytics(window.appInsights || null);
    analytics.trackPageView();

    const appsGrid = document.querySelector(`.${SELECTORS.APPS_GRID}`);
    if (appsGrid) {
        const renderer = new AppCardRenderer(appsGrid);
        const apps = await renderer.loadApps();

        if (apps) {
            const sortSelect = document.getElementById(SELECTORS.SORT_SELECT);
            if (sortSelect) {
                new AppSorter(appsGrid, sortSelect);
            }

            const showDisabled = document.getElementById('show-disabled');
            if (showDisabled) {
                renderer.filterDisabled(false);
                showDisabled.addEventListener('change', (e) => renderer.filterDisabled(e.target.checked));
            }
        }
    }
});


