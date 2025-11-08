/**
 * Sort Strategies - Strategy Pattern Implementation
 * Separates sorting algorithms for better maintainability
 */

import { SORT_TYPES, APP_STATUS } from '../constants/app-constants.js';

/**
 * Status order for sorting
 */
const STATUS_ORDER = {
    [APP_STATUS.ACTIVE]: 0,
    [APP_STATUS.DISABLED]: 1,
    [APP_STATUS.BROKEN]: 2
};

/**
 * Sort strategies object
 */
export const sortStrategies = {
    /**
     * Sort alphabetically A-Z
     */
    [SORT_TYPES.ALPHA_ASC]: (a, b) => {
        return a.dataset.name.localeCompare(b.dataset.name);
    },

    /**
     * Sort alphabetically Z-A
     */
    [SORT_TYPES.ALPHA_DESC]: (a, b) => {
        return b.dataset.name.localeCompare(a.dataset.name);
    },

    /**
     * Sort by status (Active first, then disabled, then broken)
     * Secondary sort by name
     */
    [SORT_TYPES.STATUS]: (a, b) => {
        const statusA = STATUS_ORDER[a.dataset.status] ?? 999;
        const statusB = STATUS_ORDER[b.dataset.status] ?? 999;
        const statusDiff = statusA - statusB;
        
        return statusDiff !== 0 
            ? statusDiff 
            : a.dataset.name.localeCompare(b.dataset.name);
    },

    /**
     * Sort by category, then by name
     */
    [SORT_TYPES.CATEGORY]: (a, b) => {
        const categoryDiff = a.dataset.category.localeCompare(b.dataset.category);
        
        return categoryDiff !== 0 
            ? categoryDiff 
            : a.dataset.name.localeCompare(b.dataset.name);
    }
};

/**
 * Get sort strategy by type
 * @param {string} sortType - Sort type from SORT_TYPES
 * @returns {Function} Sort function
 */
export function getSortStrategy(sortType) {
    return sortStrategies[sortType] || sortStrategies[SORT_TYPES.ALPHA_ASC];
}
