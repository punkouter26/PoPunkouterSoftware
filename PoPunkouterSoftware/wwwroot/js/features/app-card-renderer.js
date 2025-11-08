/**
 * App Card Renderer - Renders app cards from JSON data
 * Implements data-driven UI pattern
 */

import { 
    APP_STATUS, 
    CATEGORY_LABELS, 
    STATUS_LABELS, 
    STATUS_ICONS,
    SELECTORS 
} from '../constants/app-constants.js';
import { eventBus } from '../core/event-bus.js';
import { EVENT_NAMES } from '../constants/app-constants.js';

class AppCardRenderer {
    constructor(containerElement) {
        this.container = containerElement;
        this.apps = [];
    }

    /**
     * Load apps from JSON file
     */
    async loadApps() {
        try {
            const response = await fetch('/data/apps.json');
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            const data = await response.json();
            this.apps = data.apps;
            this.render();
            
            eventBus.emit(EVENT_NAMES.APPS_LOADED, { 
                count: this.apps.length 
            });
            
            return this.apps;
        } catch (error) {
            console.error('Failed to load apps:', error);
            this.renderError();
            throw error;
        }
    }

    /**
     * Render all app cards
     */
    render() {
        if (!this.container) {
            console.error('Container element not found');
            return;
        }

        this.container.innerHTML = '';
        this.apps.forEach(app => {
            const card = this.createCard(app);
            this.container.appendChild(card);
        });

        this.updateAppCount();
    }

    /**
     * Create a single app card element
     * @param {Object} app - App data
     * @returns {HTMLElement} Card element
     */
    createCard(app) {
        const card = document.createElement('div');
        card.className = SELECTORS.APP_CARD;
        card.dataset.status = app.status;
        card.dataset.category = app.category;
        card.dataset.name = app.name;

        card.innerHTML = `
            <div class="card-header">
                <h3 class="app-name">${app.name}</h3>
                ${this.createStatusBadge(app.status)}
            </div>
            ${this.createCategoryTag(app.category)}
            <p class="app-description">${app.description}</p>
            ${this.createTechStack(app.technologies)}
            ${this.createAppLink(app.url, app.status)}
        `;

        return card;
    }

    /**
     * Create status badge HTML
     * @param {string} status - App status
     * @returns {string} HTML string
     */
    createStatusBadge(status) {
        const icon = STATUS_ICONS[status] || '?';
        const label = STATUS_LABELS[status] || 'Unknown';
        
        return `
            <span class="status-badge status-${status}">
                <span class="status-icon">${icon}</span>
                <span class="status-text">${label}</span>
            </span>
        `;
    }

    /**
     * Create category tag HTML
     * @param {string} category - App category
     * @returns {string} HTML string
     */
    createCategoryTag(category) {
        const label = CATEGORY_LABELS[category] || category;
        return `<div class="category-tag tag-${category}">${label}</div>`;
    }

    /**
     * Create technology stack badges HTML
     * @param {Array} technologies - Array of technology names
     * @returns {string} HTML string
     */
    createTechStack(technologies) {
        const badges = technologies
            .map(tech => `<span class="tech-badge">${tech}</span>`)
            .join('');
        
        return `<div class="tech-stack">${badges}</div>`;
    }

    /**
     * Create app link button HTML
     * @param {string} url - App URL
     * @param {string} status - App status
     * @returns {string} HTML string
     */
    createAppLink(url, status) {
        const disabledClass = status !== APP_STATUS.ACTIVE ? ' disabled' : '';
        
        return `
            <a href="${url}" 
               target="_blank" 
               class="app-link${disabledClass}"
               ${status !== APP_STATUS.ACTIVE ? 'aria-disabled="true"' : ''}>
                <span>Visit Site</span>
                <span class="link-icon">→</span>
            </a>
        `;
    }

    /**
     * Update app count display
     */
    updateAppCount() {
        const countElement = document.getElementById(SELECTORS.APP_COUNT);
        if (countElement) {
            countElement.textContent = `${this.apps.length} apps`;
        }
    }

    /**
     * Render error message
     */
    renderError() {
        if (!this.container) return;
        
        this.container.innerHTML = `
            <div class="error-message">
                <p>Failed to load applications. Please try again later.</p>
            </div>
        `;
    }
}

export default AppCardRenderer;
