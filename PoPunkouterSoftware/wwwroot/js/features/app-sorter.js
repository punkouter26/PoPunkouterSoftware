import { SORT_TYPES, APP_STATUS, SELECTORS } from '../constants/app-constants.js';

const STATUS_ORDER = {
    [APP_STATUS.ACTIVE]: 0,
    [APP_STATUS.DISABLED]: 1,
    [APP_STATUS.BROKEN]: 2
};

const sortStrategies = {
    [SORT_TYPES.ALPHA_ASC]: (a, b) => a.dataset.name.localeCompare(b.dataset.name),
    [SORT_TYPES.STATUS]: (a, b) => {
        const diff = (STATUS_ORDER[a.dataset.status] ?? 999) - (STATUS_ORDER[b.dataset.status] ?? 999);
        return diff !== 0 ? diff : a.dataset.name.localeCompare(b.dataset.name);
    }
};

export function sortCardsByType(cards, sortType) {
    const strategy = sortStrategies[sortType] || sortStrategies[SORT_TYPES.ALPHA_ASC];
    return [...cards].sort(strategy);
}

class AppSorter {
    constructor(gridElement, selectElement) {
        if (!gridElement || !selectElement) return;
        this.grid = gridElement;
        this.select = selectElement;
        this.select.addEventListener('change', () => this.handleSort());
    }

    handleSort() {
        const sortType = this.select.value;
        const cards = Array.from(this.grid.querySelectorAll(`.${SELECTORS.APP_CARD}`));
        const sortedCards = sortCardsByType(cards, sortType);
        sortedCards.forEach(card => this.grid.appendChild(card));
    }
}

export default AppSorter;
