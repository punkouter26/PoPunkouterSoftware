/**
 * Application Insights Initialization
 * Connects to shared PoShared Application Insights instance
 * Prefixes all telemetry with PoPunkouterSoftware for identification
 */

// Microsoft Application Insights JavaScript SDK snippet
!function(T,l,y){var S=T.location,k="script",D="instrumentationKey",C="ingestionendpoint",I="disableExceptionTracking",E="ai.device.",b="toLowerCase",w=(D[b](),"]]>"),A="track",N="TrackPage",B="TrackEvent",H="crossOrigin",M=null,L=null;(function(n){var t=T[n]=T[n]||function(){(t.q=t.q||[]).push(arguments)};t.q=t.q||[],t.version=2,t.config={instrumentationKey:"85672f16-e8e5-4f0f-882f-1ca7eff6b93f",disableFetchTracking:!0,disableAjaxTracking:!0,enableAutoRouteTracking:!0}})("appInsights");var O=l.createElement(k);O.src="https://js.monitor.azure.com/scripts/b/ai.3.gbl.min.js",O.crossOrigin="anonymous",O.onload=function(){try{T[D]=T.appInsights.config.instrumentationKey;var e=T.appInsights;e.queue&&0===e.queue.length&&(e.queue.push(function(){e.addTelemetryInitializer(function(t){return t.tags=t.tags||{},t.tags["ai.cloud.role"]="PoPunkouterSoftware",t.tags["ai.cloud.roleInstance"]="StaticWebApp",!0})}),e.trackPageView({name:document.title})),console.log("Application Insights initialized for PoPunkouterSoftware")}catch(e){console.warn("App Insights init error:",e)}},O.onerror=function(){console.warn("Failed to load Application Insights SDK")},l.head.appendChild(O)}(window,document);
