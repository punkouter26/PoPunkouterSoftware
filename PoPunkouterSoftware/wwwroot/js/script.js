// Track custom events when users interact with the page
document.addEventListener('DOMContentLoaded', function() {
    // Track when rotating images load
    const rotatingImages = document.querySelectorAll('.rotating-image');
    if (rotatingImages.length > 0) {
        if (window.appInsights) {
            window.appInsights.trackEvent({name: "RotatingImagesLoaded", properties: {count: rotatingImages.length}});
        }
    }

    // Track navigation link clicks
    document.addEventListener('click', function(e) {
        if (e.target.tagName === 'A') {
            if (window.appInsights) {
                window.appInsights.trackEvent({
                    name: "LinkClicked",
                    properties: {
                        linkText: e.target.innerText || 'Unknown',
                        destination: e.target.href
                    }
                });
            }
        }
    });
});
