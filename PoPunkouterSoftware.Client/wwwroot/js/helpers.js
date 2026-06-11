/**
 * Trigger a browser file download with the given text content.
 * @param {string} filename  - suggested filename (e.g. "azure-report-2026-05-14.json")
 * @param {string} content   - file content (JSON string)
 * @param {string} mimeType  - MIME type (default: application/json)
 */
window.downloadTextFile = function (filename, content, mimeType) {
    mimeType = mimeType || 'application/json';
    const blob = new Blob([content], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};

window.poAppStorage = {
    get: function (key) {
        return window.localStorage.getItem(key);
    },
    set: function (key, value) {
        window.localStorage.setItem(key, value);
    },
    remove: function (key) {
        window.localStorage.removeItem(key);
    }
};

/**
 * Dynamically animate blurry interactive dynamic background nodes on a background canvas.
 */
window.initBackgroundCanvas = function () {
    const canvas = document.getElementById('app-bg-canvas');
    if (!canvas) return;
    if (window.bgCanvasElement === canvas) return;

    // Clean up previous listeners
    if (window.bgCanvasResizeHandler) {
        window.removeEventListener('resize', window.bgCanvasResizeHandler);
    }
    if (window.bgCanvasMouseMoveHandler) {
        window.removeEventListener('mousemove', window.bgCanvasMouseMoveHandler);
    }

    window.bgCanvasElement = canvas;
    const ctx = canvas.getContext('2d');
    let width = canvas.width = window.innerWidth;
    let height = canvas.height = window.innerHeight;

    window.bgCanvasResizeHandler = () => {
        width = canvas.width = window.innerWidth;
        height = canvas.height = window.innerHeight;
    };
    window.addEventListener('resize', window.bgCanvasResizeHandler);

    const colors = ['#49c7a3', '#8de2c5', '#f8c36b', '#ff8a8a', '#9fdfff'];
    const nodes = [];
    const numNodes = 8;

    for (let i = 0; i < numNodes; i++) {
        nodes.push({
            x: Math.random() * width,
            y: Math.random() * height,
            vx: (Math.random() - 0.5) * 0.6,
            vy: (Math.random() - 0.5) * 0.6,
            radius: Math.random() * 140 + 100,
            color: colors[i % colors.length]
        });
    }

    let mouse = { x: width / 2, y: height / 2, tx: width / 2, ty: height / 2 };
    window.bgCanvasMouseMoveHandler = (e) => {
        mouse.tx = e.clientX;
        mouse.ty = e.clientY;
    };
    window.addEventListener('mousemove', window.bgCanvasMouseMoveHandler);

    function animate() {
        if (window.bgCanvasElement !== canvas) return; // Stop animation loop if canvas changes
        ctx.clearRect(0, 0, width, height);

        // Fluid spring interpolation
        mouse.x += (mouse.tx - mouse.x) * 0.04;
        mouse.y += (mouse.ty - mouse.y) * 0.04;

        for (let n of nodes) {
            n.x += n.vx;
            n.y += n.vy;

            // Bouncing/wrapping check
            if (n.x < -n.radius) n.x = width + n.radius;
            if (n.x > width + n.radius) n.x = -n.radius;
            if (n.y < -n.radius) n.y = height + n.radius;
            if (n.y > height + n.radius) n.y = -n.radius;

            // Interactive repulsion from pointer
            const dx = n.x - mouse.x;
            const dy = n.y - mouse.y;
            const dist = Math.sqrt(dx * dx + dy * dy);
            if (dist < 400) {
                const force = (400 - dist) * 0.04;
                n.x += (dx / dist) * force;
                n.y += (dy / dist) * force;
            }

            ctx.beginPath();
            ctx.arc(n.x, n.y, n.radius, 0, Math.PI * 2);
            ctx.fillStyle = n.color;
            ctx.fill();
        }

        requestAnimationFrame(animate);
    }
    animate();
};

/**
 * Attaches a 3D parallax tilt & reflection gloss style tracker.
 */
window.initTiltCard = function (element) {
    if (!element) return;
    const handleMove = (e) => {
        const rect = element.getBoundingClientRect();
        const x = e.clientX - rect.left;
        const y = e.clientY - rect.top;
        const xc = rect.width / 2;
        const yc = rect.height / 2;
        const dx = x - xc;
        const dy = y - yc;
        const rx = -(dy / yc) * 6; // Limit to 6deg
        const ry = (dx / xc) * 6;
        element.style.setProperty('--rx', rx + 'deg');
        element.style.setProperty('--ry', ry + 'deg');
        
        const px = (x / rect.width) * 100;
        const py = (y / rect.height) * 100;
        element.style.setProperty('--glow-x', px + '%');
        element.style.setProperty('--glow-y', py + '%');
    };
    const handleLeave = () => {
        element.style.setProperty('--rx', '0deg');
        element.style.setProperty('--ry', '0deg');
        element.style.setProperty('--glow-x', '50%');
        element.style.setProperty('--glow-y', '50%');
    };
    element.addEventListener('mousemove', handleMove);
    element.addEventListener('mouseleave', handleLeave);
};

/**
 * Track page scroll state to float/hide navbar navigation bar.
 */
window.initNavbarScroll = function () {
    const header = document.querySelector('.rz-header');
    if (!header) return;
    if (window.navbarScrollHeader === header) return;

    if (window.navbarScrollHandler) {
        window.removeEventListener('scroll', window.navbarScrollHandler);
    }

    window.navbarScrollHeader = header;
    let lastScrollY = window.scrollY;
    
    window.navbarScrollHandler = () => {
        if (window.navbarScrollHeader !== header) return;
        const currentScrollY = window.scrollY;
        if (currentScrollY > lastScrollY && currentScrollY > 80) {
            header.classList.add('nav-hidden');
        } else {
            header.classList.remove('nav-hidden');
        }
        if (currentScrollY > 20) {
            header.classList.add('nav-scrolled');
        } else {
            header.classList.remove('nav-scrolled');
        }
        lastScrollY = currentScrollY;
    };
    window.addEventListener('scroll', window.navbarScrollHandler);
};

/**
 * One-tap copy clipboard helper with a micro-notification overlay
 */
window.copyToClipboard = function (text) {
    navigator.clipboard.writeText(text).then(() => {
        const toast = document.createElement('div');
        toast.className = 'app-micro-toast';
        toast.innerText = 'Copied suggested fix!';
        document.body.appendChild(toast);
        setTimeout(() => toast.classList.add('show'), 20);
        setTimeout(() => {
            toast.classList.remove('show');
            setTimeout(() => {
                if (toast.parentNode) {
                    document.body.removeChild(toast);
                }
            }, 300);
        }, 1800);
    }).catch(err => {
        console.error('Clipboard copy failed: ', err);
    });
};
