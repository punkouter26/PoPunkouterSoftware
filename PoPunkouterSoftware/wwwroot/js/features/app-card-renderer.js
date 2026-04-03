import {
    APP_STATUS,
    APP_CATEGORIES,
    CATEGORY_LABELS,
    STATUS_DISPLAY,
    SELECTORS
} from '../constants/app-constants.js';

class AppCardRenderer {
    constructor(containerElement) {
        this.container = containerElement;
        this.apps = [];
    }

    renderSkeleton(count = 3) {
        this.container.innerHTML = '';
        for (let i = 0; i < count; i++) {
            const card = document.createElement('div');
            card.className = 'app-card skeleton-card';
            card.innerHTML = `
                <div class="skeleton-line skeleton-title"></div>
                <div class="skeleton-line skeleton-desc"></div>
                <div class="skeleton-line skeleton-desc short"></div>
                <div class="skeleton-tech"></div>
                <div class="skeleton-line skeleton-btn"></div>
            `;
            this.container.appendChild(card);
        }
    }

    async loadApps() {
        this.renderSkeleton(3);
        try {
            const response = await fetch('/data/apps.json');
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            const data = await response.json();
            this.apps = data.apps;
            this.render();
            return this.apps;
        } catch (error) {
            console.error('Failed to load apps:', error);
            this.renderError();
            return null;
        }
    }

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

    createCard(app) {
        // Validate against allow-lists before injecting into CSS class names
        const validStatuses    = Object.values(APP_STATUS);
        const validCategories  = Object.values(APP_CATEGORIES);
        const safeStatus   = validStatuses.includes(app.status)   ? app.status   : APP_STATUS.DISABLED;
        const safeCategory = validCategories.includes(app.category) ? app.category : APP_CATEGORIES.PRODUCTIVITY;

        const { label, icon } = STATUS_DISPLAY[safeStatus] || { label: 'Unknown', icon: '?' };
        const isActive = safeStatus === APP_STATUS.ACTIVE;
        const safeUrl = isActive && /^https?:\/\//i.test(app.url) ? app.url : '#';
        const techsHtml = (app.technologies || []).map(t => `<span class="tech-badge">${this.escapeHtml(t)}</span>`).join('');
        const categoryLabel = CATEGORY_LABELS[safeCategory] || safeCategory;
        const linkHtml = isActive
            ? `<a href="${safeUrl}" target="_blank" rel="noopener noreferrer" class="app-link"><span>Visit Site</span><span class="link-icon">→</span></a>`
            : `<button type="button" class="app-link disabled" disabled aria-disabled="true"><span>Visit Site</span><span class="link-icon">→</span></button>`;

        const card = document.createElement('div');
        card.className = SELECTORS.APP_CARD;
        card.dataset.status   = safeStatus;
        card.dataset.category = safeCategory;
        card.dataset.name     = app.name;
        card.innerHTML = `
            <div class="card-header">
                <h3 class="app-name">${this.escapeHtml(app.name)}</h3>
                <span class="status-badge status-${safeStatus}"><span class="status-icon">${icon}</span><span class="status-text">${label}</span></span>
            </div>
            <div class="category-tag tag-${safeCategory}">${categoryLabel}</div>
            <p class="app-description">${this.escapeHtml(app.description)}</p>
            <div class="tech-stack">${techsHtml}</div>
            ${linkHtml}
        `;
        return card;
    }

    updateAppCount() {
        const countElement = document.getElementById(SELECTORS.APP_COUNT);
        if (countElement) {
            countElement.textContent = `${this.apps.length} apps`;
        }
    }

    renderError() {
        if (!this.container) return;
        this.container.innerHTML = '<p class="error-message">Failed to load applications. Please try again later.</p>';
    }

    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }
}

export default AppCardRenderer;
