/**
 * Kinetic typography and smooth rolling numeric ticker animations.
 * Smoothly interpolates numeric text in KPI elements across a 400ms ease curve.
 */
(function () {
    'use strict';

    function easeOutQuad(t) {
        return t * (2 - t);
    }

    function animateNumber(element, start, end, duration, formatFn) {
        var startTime = performance.now();
        function update(currentTime) {
            var elapsed = currentTime - startTime;
            var progress = Math.min(elapsed / duration, 1);
            var eased = easeOutQuad(progress);
            var current = start + (end - start) * eased;
            element.textContent = formatFn ? formatFn(current) : Math.round(current).toString();
            if (progress < 1) {
                requestAnimationFrame(update);
            }
        }
        requestAnimationFrame(update);
    }

    window.numericTicker = {
        animate: function (selector, targetValue, duration) {
            var el = document.querySelector(selector);
            if (!el) return;
            var text = el.textContent || '';
            var start = parseFloat(text.replace(/[^0-9.-]+/g, '')) || 0;
            var end = parseFloat(targetValue) || 0;
            animateNumber(el, start, end, duration || 400);
        }
    };
})();
