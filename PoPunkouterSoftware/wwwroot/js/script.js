document.addEventListener('DOMContentLoaded', () => {
    // Initialize navigation
    initializeNavigation();
    
    // Initialize image rotation if on home page
    if (isHomePage()) {
        initializeImageRotation();
    }
});

function initializeNavigation() {
    fetch('navigation.html')
        .then(response => response.text())
        .then(text => {
            document.getElementById('navigation-placeholder').innerHTML = text;
        })
        .catch(error => console.error('Error loading navigation:', error));
}

function initializeImageRotation() {
    const images = document.querySelectorAll('.rotating-image');
    if (!images.length) return;

    // Initialize image states
    let currentImage = 0;
    images[0].style.display = 'block';
    images.forEach((img, index) => {
        if (index !== 0) img.style.display = 'none';
        img.style.transition = 'opacity 0.5s ease-in-out';
    });

    // Set up rotation
    setInterval(() => rotateImage(images, currentImage++), 2000);
    currentImage %= images.length;
}

function rotateImage(images, current) {
    const next = (current + 1) % images.length;
    
    // Fade out current image
    images[current].style.opacity = '0';
    
    // Switch images after fade
    setTimeout(() => {
        images[current].style.display = 'none';
        images[next].style.display = 'block';
        
        // Trigger fade in
        requestAnimationFrame(() => {
            images[next].style.opacity = '1';
        });
    }, 500);
}

function isHomePage() {
    const path = window.location.pathname;
    return path.endsWith('index.html') || path === '/' || path === '';
}