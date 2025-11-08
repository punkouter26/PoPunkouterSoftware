/**
 * App Sorter - Handles sorting of application cards
 * Follows Single Responsibility Principle
 */

import { getSortStrategy } from './sort-strategies.js';
import { eventBus } from '../core/event-bus.js';
import { EVENT_NAMES, ANIMATION_TIMINGS, SELECTORS } from '../constants/app-constants.js';

class AppSorter {
    constructor(gridElement, selectElement) {
        if (!gridElement || !selectElement) {
            console.warn('AppSorter: Required elements not found');
            return;
        }
        
        this.grid = gridElement;
        this.select = selectElement;
        this.init();
    }

    /**
     * Initialize the sorter
     */
    init() {
        try {
            this.select.addEventListener('change', () => this.handleSort());
        } catch (error) {
            this.logError('Initialization failed', error);
        }
    }

    /**
     * Handle sort event
     */
    handleSort() {
        try {
            const sortType = this.select.value;
            const sortStrategy = getSortStrategy(sortType);
            const cards = Array.from(this.grid.querySelectorAll(`.${SELECTORS.APP_CARD}`));
            
            // Sort cards using strategy
            cards.sort(sortStrategy);
            
            // Animate the reordering
            this.animateReorder(cards);
            
            // Emit event for analytics
            eventBus.emit(EVENT_NAMES.APP_SORTED, { sortType });
        } catch (error) {
            this.logError('Sort operation failed', error);
        }
    }

    /**
     * Animate card reordering
     * @param {Array} cards - Sorted cards
     */
    animateReorder(cards) {
        // Remove animation delay temporarily
        cards.forEach(card => {
            card.style.animation = 'none';
        });

        // Fade out
        this.fadeOut();

        // Reorder after fade out
        setTimeout(() => {
            this.reorder(cards);
            this.restoreAnimations(cards);
            this.fadeIn();
        }, ANIMATION_TIMINGS.FADE_DURATION);
    }

    /**
     * Fade out the grid
     */
    fadeOut() {
        this.grid.style.opacity = '0.5';
        this.grid.style.transition = `opacity ${ANIMATION_TIMINGS.FADE_DURATION}ms ease`;
    }

    /**
     * Fade in the grid
     */
    fadeIn() {
        this.grid.style.opacity = '1';
    }

    /**
     * Reorder cards in DOM
     * @param {Array} cards - Sorted cards
     */
    reorder(cards) {
        cards.forEach(card => this.grid.appendChild(card));
    }

    /**
     * Restore staggered animations
     * @param {Array} cards - Cards to animate
     */
    restoreAnimations(cards) {
        requestAnimationFrame(() => {
            cards.forEach((card, index) => {
                card.style.animation = '';
                card.style.animationDelay = `${index * ANIMATION_TIMINGS.STAGGER_DELAY}ms`;
            });
        });
    }

    /**
     * Log error
     * @param {string} message - Error message
     * @param {Error} error - Error object
     */
    logError(message, error) {
        console.error(`AppSorter Error: ${message}`, error);
        eventBus.emit('error:occurred', {
            component: 'AppSorter',
            message,
            error: error.message
        });
    }
}

export default AppSorter;
