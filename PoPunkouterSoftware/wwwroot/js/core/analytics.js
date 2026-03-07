class Analytics {
    constructor(appInsights) {
        this.appInsights = appInsights;
    }

    track(name, properties = {}) {
        if (this.appInsights && typeof this.appInsights.trackEvent === 'function') {
            this.appInsights.trackEvent({ name, properties });
        }
    }

    trackPageView() {
        if (this.appInsights && typeof this.appInsights.trackPageView === 'function') {
            this.appInsights.trackPageView();
        }
    }
}

export default Analytics;
