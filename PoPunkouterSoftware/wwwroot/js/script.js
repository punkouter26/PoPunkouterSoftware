document.addEventListener('DOMContentLoaded', () => {
    // Initialize navigation
    initializeNavigation();
    
    // Initialize image rotation if on home page
    if (isHomePage()) {
        initializeImageRotation();
    }
});

function initializeNavigation() {
    const placeholder = document.getElementById('navigation-placeholder');
    
    // Add loading state
    placeholder.innerHTML = '<nav aria-label="Loading navigation"><p>Loading navigation...</p></nav>';
    
    fetch('navigation.html')
        .then(response => {
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}: ${response.statusText}`);
            }
            return response.text();
        })
        .then(text => {
            placeholder.innerHTML = text;
            
            // Enhance navigation accessibility
            const nav = placeholder.querySelector('nav');
            if (nav && !nav.getAttribute('aria-label')) {
                nav.setAttribute('aria-label', 'Main navigation');
            }
            
            // Track successful navigation load
            if (window.appInsights) {
                window.appInsights.trackEvent({
                    name: "NavigationLoaded",
                    properties: { success: true }
                });
            }
        })
        .catch(error => {
            console.error('Error loading navigation:', error);
            placeholder.innerHTML = `
                <nav aria-label="Navigation error">
                    <p>Navigation temporarily unavailable</p>
                    <button onclick="location.reload()" aria-label="Reload page to retry navigation">
                        Retry
                    </button>
                </nav>
            `;
            
            // Track navigation error
            if (window.appInsights) {
                window.appInsights.trackEvent({
                    name: "NavigationError",
                    properties: { 
                        error: error.message,
                        success: false 
                    }
                });
            }
        });
}

function initializeImageRotation() {
    const images = document.querySelectorAll('.rotating-image');
    if (!images.length) return;

    // Check if user prefers reduced motion
    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    
    let currentIndex = 0;
    
    // Initialize first image
    images.forEach((img, index) => {
        img.style.opacity = index === 0 ? '1' : '0';
        img.style.position = 'absolute';
        img.style.transition = prefersReducedMotion ? 'none' : 'opacity 0.8s ease-in-out';
        img.style.top = '0';
        img.style.left = '0';
    });

    // Skip rotation if user prefers reduced motion
    if (prefersReducedMotion) {
        console.log('Image rotation disabled due to reduced motion preference');
        return;
    }

    function rotateImages() {
        const nextIndex = (currentIndex + 1) % images.length;
        
        images[currentIndex].style.opacity = '0';
        images[nextIndex].style.opacity = '1';
        
        currentIndex = nextIndex;
        
        // Track rotation event
        if (window.appInsights) {
            window.appInsights.trackEvent({
                name: "ImageRotated", 
                properties: { imageIndex: currentIndex }
            });
        }
    }

    setInterval(rotateImages, 3000); // Slower, more pleasant transition
}

function isHomePage() {
    const path = window.location.pathname;
    return path.endsWith('index.html') || path === '/' || path === '';
}

// Track custom events when users interact with the page
document.addEventListener('DOMContentLoaded', function() {
    // Track when rotating images change
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
