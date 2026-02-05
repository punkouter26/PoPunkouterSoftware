/**
 * Application Insights Initialization
 * Connects to shared PoShared Application Insights instance
 * Prefixes all telemetry with PoPunkouterSoftware for identification
 */

(function() {
    // Application Insights SDK loader
    const sdkScript = document.createElement('script');
    sdkScript.src = 'https://js.monitor.azure.com/scripts/b/ai.3.gbl.min.js';
    sdkScript.crossOrigin = 'anonymous';
    
    sdkScript.onload = function() {
        const connectionString = 'InstrumentationKey=85672f16-e8e5-4f0f-882f-1ca7eff6b93f;IngestionEndpoint=https://eastus2-3.in.applicationinsights.azure.com/;LiveEndpoint=https://eastus2.livediagnostics.monitor.azure.com/';
        
        const config = {
            connectionString: connectionString,
            enableAutoRouteTracking: true,
            autoTrackPageVisitTime: true
        };

        // Initialize Application Insights
        const appInsights = new Microsoft.ApplicationInsights.ApplicationInsights({ config });
        appInsights.loadAppInsights();
        
        // Add cloud role name for identification in shared App Insights
        appInsights.addTelemetryInitializer((envelope) => {
            envelope.tags = envelope.tags || [];
            envelope.tags['ai.cloud.role'] = 'PoPunkouterSoftware';
            envelope.tags['ai.cloud.roleInstance'] = 'StaticWebApp';
            
            // Prefix custom event names with app name
            if (envelope.baseData && envelope.baseData.name && !envelope.baseData.name.startsWith('PoPunkouterSoftware_')) {
                envelope.baseData.name = 'PoPunkouterSoftware_' + envelope.baseData.name;
            }
        });

        // Track initial page view
        appInsights.trackPageView();
        
        // Expose globally for analytics module
        window.appInsights = appInsights;
        
        console.log('Application Insights initialized for PoPunkouterSoftware');
    };

    document.head.appendChild(sdkScript);
})();
