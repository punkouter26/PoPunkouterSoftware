/**
 * Application Constants
 * Centralized constants to avoid magic strings and improve maintainability
 */

export const SORT_TYPES = {
    ALPHA_ASC: 'alphabetical',
    STATUS: 'status'
};

export const APP_STATUS = {
    ACTIVE: 'active',
    DISABLED: 'disabled',
    BROKEN: 'broken'
};

export const APP_CATEGORIES = {
    GAMES: 'games',
    AI: 'ai',
    PRODUCTIVITY: 'productivity',
    CREATIVE: 'creative'
};

export const CATEGORY_LABELS = {
    [APP_CATEGORIES.GAMES]: '🎮 Games',
    [APP_CATEGORIES.AI]: '🤖 AI Tools',
    [APP_CATEGORIES.PRODUCTIVITY]: '📊 Productivity',
    [APP_CATEGORIES.CREATIVE]: '🎨 Creative'
};

export const STATUS_DISPLAY = {
    [APP_STATUS.ACTIVE]: { label: 'Active', icon: '●' },
    [APP_STATUS.DISABLED]: { label: 'Disabled', icon: '○' },
    [APP_STATUS.BROKEN]: { label: 'Broken', icon: '⚠' }
};

export const SELECTORS = {
    APP_CARD: 'app-card',
    APP_COUNT: 'app-count-text'
};


