/**
 * Analytics Module
 * Handles application insights tracking using event bus pattern
 */

import { eventBus } from './event-bus.js';
import { EVENT_NAMES, ANALYTICS_EVENTS } from '../constants/app-constants.js';

class Analytics {
    constructor(appInsights) {
        this.appInsights = appInsights;
        this.setupListeners();
    }

    /**
     * Setup event listeners for analytics tracking
     */
    setupListeners() {
        eventBus.on(EVENT_NAMES.APP_SORTED, (data) => 
            this.track(ANALYTICS_EVENTS.APPS_SORTED, data)
        );
        
        eventBus.on(EVENT_NAMES.LINK_CLICKED, (data) => 
            this.track(ANALYTICS_EVENTS.LINK_CLICKED, data)
        );
        
        eventBus.on(EVENT_NAMES.APPS_LOADED, (data) => 
            this.track(ANALYTICS_EVENTS.APPS_LOADED, data)
        );
    }

    /**
     * Track an event
     * @param {string} name - Event name
     * @param {Object} properties - Event properties
     */
    track(name, properties = {}) {
        if (this.appInsights) {
            this.appInsights.trackEvent({ name, properties });
        } else {
            console.log('Analytics:', name, properties);
        }
    }

    /**
     * Track a page view
     */
    trackPageView() {
        if (this.appInsights) {
            this.appInsights.trackPageView();
        }
    }

    /**
     * Track an exception
     * @param {Error} error - Error object
     * @param {Object} properties - Additional properties
     */
    trackException(error, properties = {}) {
        if (this.appInsights) {
            this.appInsights.trackException({ 
                exception: error, 
                properties 
            });
        } else {
            console.error('Exception:', error, properties);
        }
    }
}

export default Analytics;
