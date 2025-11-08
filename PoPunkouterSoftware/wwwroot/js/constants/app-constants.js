/**
 * Application Constants
 * Centralized constants to avoid magic strings and improve maintainability
 */

export const SORT_TYPES = {
    ALPHA_ASC: 'alphabetical',
    ALPHA_DESC: 'alphabetical-desc',
    STATUS: 'status',
    CATEGORY: 'category'
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

export const STATUS_LABELS = {
    [APP_STATUS.ACTIVE]: 'Active',
    [APP_STATUS.DISABLED]: 'Disabled',
    [APP_STATUS.BROKEN]: 'Broken'
};

export const STATUS_ICONS = {
    [APP_STATUS.ACTIVE]: '●',
    [APP_STATUS.DISABLED]: '○',
    [APP_STATUS.BROKEN]: '⚠'
};

export const ANIMATION_TIMINGS = {
    FADE_DURATION: 300,
    STAGGER_DELAY: 50,
    CARD_TRANSITION: 400
};

export const SELECTORS = {
    SORT_SELECT: 'sort-select',
    APPS_GRID: 'apps-grid',
    APP_CARD: 'app-card',
    APP_COUNT: 'app-count-text'
};

export const EVENT_NAMES = {
    APP_SORTED: 'app:sorted',
    LINK_CLICKED: 'link:clicked',
    APPS_LOADED: 'apps:loaded'
};

export const ANALYTICS_EVENTS = {
    APPS_SORTED: 'AppsSorted',
    LINK_CLICKED: 'LinkClicked',
    APPS_LOADED: 'AppsLoaded'
};
