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

class AppCardRenderer {
    constructor(containerElement) {
        this.container = containerElement;
        this.apps = [];
        this._showDisabled = true;
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

    createCard(app) {
        const card = document.createElement('div');
        card.className = SELECTORS.APP_CARD;
        card.dataset.status = app.status;
        card.dataset.category = app.category;
        card.dataset.name = app.name;

        const header = document.createElement('div');
        header.className = 'card-header';

        const h3 = document.createElement('h3');
        h3.className = 'app-name';
        h3.textContent = app.name;
        header.appendChild(h3);
        header.appendChild(this.createStatusBadge(app.status));
        card.appendChild(header);

        card.appendChild(this.createCategoryTag(app.category));

        const desc = document.createElement('p');
        desc.className = 'app-description';
        desc.textContent = app.description;
        card.appendChild(desc);

        card.appendChild(this.createTechStack(app.technologies));
        card.appendChild(this.createAppLink(app.url, app.status));

        return card;
    }

    createStatusBadge(status) {
        const span = document.createElement('span');
        span.className = `status-badge status-${status}`;

        const iconSpan = document.createElement('span');
        iconSpan.className = 'status-icon';
        iconSpan.textContent = STATUS_ICONS[status] || '?';

        const textSpan = document.createElement('span');
        textSpan.className = 'status-text';
        textSpan.textContent = STATUS_LABELS[status] || 'Unknown';

        span.appendChild(iconSpan);
        span.appendChild(textSpan);
        return span;
    }

    createCategoryTag(category) {
        const div = document.createElement('div');
        div.className = `category-tag tag-${category}`;
        div.textContent = CATEGORY_LABELS[category] || category;
        return div;
    }

    createTechStack(technologies) {
        const div = document.createElement('div');
        div.className = 'tech-stack';
        (technologies || []).forEach(tech => {
            const badge = document.createElement('span');
            badge.className = 'tech-badge';
            badge.textContent = tech;
            div.appendChild(badge);
        });
        return div;
    }

    createAppLink(url, status) {
        const isActive = status === APP_STATUS.ACTIVE;
        const safeUrl = isActive && /^https?:\/\//i.test(url) ? url : '#';

        const textSpan = document.createElement('span');
        textSpan.textContent = 'Visit Site';
        const iconSpan = document.createElement('span');
        iconSpan.className = 'link-icon';
        iconSpan.textContent = '→';

        if (!isActive) {
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'app-link disabled';
            btn.disabled = true;
            btn.setAttribute('aria-disabled', 'true');
            btn.appendChild(textSpan);
            btn.appendChild(iconSpan);
            return btn;
        }

        const a = document.createElement('a');
        a.href = safeUrl;
        a.target = '_blank';
        a.rel = 'noopener noreferrer';
        a.className = 'app-link';
        a.appendChild(textSpan);
        a.appendChild(iconSpan);
        return a;
    }

    filterDisabled(show) {
        this._showDisabled = show;
        const cards = this.container.querySelectorAll(`.${SELECTORS.APP_CARD}[data-status="disabled"]`);
        cards.forEach(card => { card.style.display = show ? '' : 'none'; });
        this.updateAppCount();
    }

    updateAppCount() {
        const countElement = document.getElementById(SELECTORS.APP_COUNT);
        if (countElement) {
            const count = this._showDisabled
                ? this.apps.length
                : this.apps.filter(a => a.status === APP_STATUS.ACTIVE).length;
            countElement.textContent = `${count} apps`;
        }
    }

    renderError() {
        if (!this.container) return;
        const p = document.createElement('p');
        p.textContent = 'Failed to load applications. Please try again later.';
        const div = document.createElement('div');
        div.className = 'error-message';
        div.appendChild(p);
        this.container.innerHTML = '';
        this.container.appendChild(div);
    }
}

export default AppCardRenderer;
