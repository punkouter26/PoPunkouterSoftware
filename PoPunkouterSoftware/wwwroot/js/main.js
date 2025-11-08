/**
 * Main Application Entry Point
 * Initializes all features and components
 */

import './components/site-components.js';
import Analytics from './core/analytics.js';
import AppSorter from './features/app-sorter.js';
import AppCardRenderer from './features/app-card-renderer.js';
import LinkTracker from './features/link-tracker.js';
import { SELECTORS } from './constants/app-constants.js';

class App {
    constructor() {
        this.analytics = new Analytics(window.appInsights);
        this.components = [];
    }

    /**
     * Initialize application
     */
    init() {
        document.addEventListener('DOMContentLoaded', () => {
            this.initializeComponents();
            this.analytics.trackPageView();
        });
    }

    /**
     * Initialize page-specific components
     */
    async initializeComponents() {
        try {
            // Initialize link tracking (global)
            new LinkTracker();

            // Initialize app cards if on apps page
            const appsGrid = document.querySelector(`.${SELECTORS.APPS_GRID}`);
            if (appsGrid) {
                await this.initializeAppsPage(appsGrid);
            }
        } catch (error) {
            console.error('Component initialization error:', error);
        }
    }

    /**
     * Initialize apps page specific features
     * @param {HTMLElement} appsGrid - Apps grid container
     */
    async initializeAppsPage(appsGrid) {
        // Render app cards from JSON
        const renderer = new AppCardRenderer(appsGrid);
        await renderer.loadApps();

        // Initialize sorter
        const sortSelect = document.getElementById(SELECTORS.SORT_SELECT);
        if (sortSelect) {
            this.components.push(new AppSorter(appsGrid, sortSelect));
        }
    }
}

// Initialize application
const app = new App();
app.init();

export default app;
