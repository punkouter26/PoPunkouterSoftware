import { SORT_TYPES, APP_STATUS, ANIMATION_TIMINGS, SELECTORS } from '../constants/app-constants.js';

const STATUS_ORDER = {
    [APP_STATUS.ACTIVE]: 0,
    [APP_STATUS.DISABLED]: 1,
    [APP_STATUS.BROKEN]: 2
};

const sortStrategies = {
    [SORT_TYPES.ALPHA_ASC]: (a, b) => a.dataset.name.localeCompare(b.dataset.name),
    [SORT_TYPES.ALPHA_DESC]: (a, b) => b.dataset.name.localeCompare(a.dataset.name),
    [SORT_TYPES.STATUS]: (a, b) => {
        const diff = (STATUS_ORDER[a.dataset.status] ?? 999) - (STATUS_ORDER[b.dataset.status] ?? 999);
        return diff !== 0 ? diff : a.dataset.name.localeCompare(b.dataset.name);
    },
    [SORT_TYPES.CATEGORY]: (a, b) => {
        const diff = a.dataset.category.localeCompare(b.dataset.category);
        return diff !== 0 ? diff : a.dataset.name.localeCompare(b.dataset.name);
    }
};

class AppSorter {
    constructor(gridElement, selectElement) {
        if (!gridElement || !selectElement) return;
        this.grid = gridElement;
        this.select = selectElement;
        this.select.addEventListener('change', () => this.handleSort());
    }

    handleSort() {
        const sortType = this.select.value;
        const strategy = sortStrategies[sortType] || sortStrategies[SORT_TYPES.ALPHA_ASC];
        const cards = Array.from(this.grid.querySelectorAll(`.${SELECTORS.APP_CARD}`));
        cards.sort(strategy);
        this.animateReorder(cards);
    }

    animateReorder(cards) {
        cards.forEach(card => card.style.animation = 'none');
        this.grid.style.opacity = '0.5';
        this.grid.style.transition = `opacity ${ANIMATION_TIMINGS.FADE_DURATION}ms ease`;
        setTimeout(() => {
            cards.forEach(card => this.grid.appendChild(card));
            requestAnimationFrame(() => {
                cards.forEach((card, i) => {
                    card.style.animation = '';
                    card.style.animationDelay = `${i * ANIMATION_TIMINGS.STAGGER_DELAY}ms`;
                });
            });
            this.grid.style.opacity = '1';
        }, ANIMATION_TIMINGS.FADE_DURATION);
    }

}

export default AppSorter;
