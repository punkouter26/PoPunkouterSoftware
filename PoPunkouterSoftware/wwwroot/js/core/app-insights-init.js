/**
 * Application Insights Initialization
 * Connects to shared PoShared Application Insights instance
 * Prefixes all telemetry with PoPunkouterSoftware for identification
 */

(function() {
    // Application Insights Click Analytics Plugin and SDK v2.8+
    const sdkScript = document.createElement('script');
    sdkScript.src = 'https://js.monitor.azure.com/scripts/b/ai.2.min.js';
    sdkScript.crossOrigin = 'anonymous';
    
    sdkScript.onload = function() {
        const connectionString = 'InstrumentationKey=85672f16-e8e5-4f0f-882f-1ca7eff6b93f;IngestionEndpoint=https://eastus2-3.in.applicationinsights.azure.com/;LiveEndpoint=https://eastus2.livediagnostics.monitor.azure.com/';
        
        // Use snippet config pattern for better compatibility
        const snippet = {
            config: {
                connectionString: connectionString,
                enableAutoRouteTracking: true,
                autoTrackPageVisitTime: true,
                disableExceptionTracking: false,
                disableAjaxTracking: true,
                disableFetchTracking: true
            }
        };

        // Initialize Application Insights using the snippet pattern
        const init = new Microsoft.ApplicationInsights.ApplicationInsights(snippet);
        const appInsights = init.loadAppInsights();
        
        // Add cloud role name for identification in shared App Insights
        appInsights.addTelemetryInitializer((envelope) => {
            envelope.tags = envelope.tags || {};
            envelope.tags['ai.cloud.role'] = 'PoPunkouterSoftware';
            envelope.tags['ai.cloud.roleInstance'] = 'StaticWebApp';
            return true;
        });

        // Track initial page view
        appInsights.trackPageView({ name: document.title });
        
        // Expose globally for analytics module
        window.appInsights = appInsights;
        
        console.log('Application Insights initialized for PoPunkouterSoftware');
    };
    
    sdkScript.onerror = function() {
        console.warn('Failed to load Application Insights SDK');
    };

    document.head.appendChild(sdkScript);
})();
