/**
 * Link Tracker - Tracks link clicks
 */

import { eventBus } from '../core/event-bus.js';
import { EVENT_NAMES } from '../constants/app-constants.js';

class LinkTracker {
    constructor() {
        this.init();
    }

    init() {
        document.addEventListener('click', (e) => this.handleClick(e));
    }

    handleClick(e) {
        if (e.target.tagName === 'A' || e.target.closest('a')) {
            const link = e.target.tagName === 'A' ? e.target : e.target.closest('a');
            
            eventBus.emit(EVENT_NAMES.LINK_CLICKED, {
                linkText: link.innerText || 'Unknown',
                destination: link.href,
                external: link.target === '_blank'
            });
        }
    }
}

export default LinkTracker;
