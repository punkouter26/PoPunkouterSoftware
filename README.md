# Punkouter Software Website

Modern, modular static website built with Vite, ES6 modules, and Web Components.

## 🏗️ Architecture Overview

This project follows a modern, maintainable architecture with:

- **Build System**: Vite 7.2.2 for fast development and optimized production builds
- **Module System**: ES6 modules with type="module"
- **Component Pattern**: Web Components for header/footer reusability
- **Data-Driven**: JSON-based app data with dynamic rendering
- **Design Patterns**: Strategy Pattern, Observer Pattern (Event Bus), Module Pattern
- **CSS Architecture**: Modular CSS with organized imports

## 📁 Project Structure

```
PoPunkouterSoftware/
├── package.json              # Node.js project configuration
├── vite.config.js            # Vite build configuration
└── wwwroot/                  # Static site root
    ├── index.html            # Homepage
    ├── OurWebApps.html       # Web apps showcase
    ├── OurTeam.html          # Team page
    ├── Contact.html          # Contact page
    ├── OurPhoneApps.html     # Phone apps page
    ├── PrivacyPolicy.html    # Privacy policy
    ├── css/
    │   ├── main.css          # CSS orchestration file
    │   ├── core/
    │   │   ├── variables.css # CSS custom properties
    │   │   └── reset.css     # CSS reset
    │   ├── layout/
    │   │   ├── main.css      # Base layout & typography
    │   │   ├── header.css    # Header & navigation
    │   │   └── footer.css    # Footer
    │   ├── components/
    │   │   ├── cards.css     # App card system
    │   │   ├── badges.css    # Status & category badges
    │   │   ├── buttons.css   # Button styles
    │   │   ├── controls.css  # Controls bar & dropdowns
    │   │   └── gallery.css   # Image gallery
    │   ├── pages/
    │   │   ├── home.css      # Homepage-specific styles
    │   │   ├── team.css      # Team page styles
    │   │   └── contact.css   # Contact page styles
    │   └── utils/
    │       ├── animations.css    # Keyframe animations
    │       └── legacy-table.css  # Legacy table styles
    ├── js/
    │   ├── main.js                 # Application entry point
    │   ├── constants/
    │   │   └── app-constants.js    # Centralized constants
    │   ├── core/
    │   │   ├── event-bus.js        # Observer pattern implementation
    │   │   └── analytics.js        # Application Insights wrapper
    │   ├── features/
    │   │   ├── sort-strategies.js  # Strategy pattern for sorting
    │   │   ├── app-sorter.js       # App card sorter
    │   │   ├── app-card-renderer.js # Data-driven rendering
    │   │   ├── link-tracker.js     # Link click tracking
    │   │   └── image-gallery.js    # Gallery initialization
    │   └── components/
    │       └── site-components.js  # Web Components (header/footer)
    ├── data/
    │   └── apps.json              # Application data
    └── images/                     # Static images
```

## 🚀 Getting Started

### Prerequisites

- Node.js (v14 or higher)
- npm or yarn

### Installation

```bash
# Install dependencies
npm install
```

### Development

```bash
# Start development server (with hot reload)
npm run dev

# Server runs at http://localhost:3000
```

### Production Build

```bash
# Build for production
npm run build

# Preview production build
npm run preview
```

### Build Output

Production files are generated in the `dist/` directory, optimized and minified.

## 🎨 CSS Architecture

The CSS is organized into modular files for better maintainability:

- **core/**: Foundation (variables, reset)
- **layout/**: Page structure (header, footer, main)
- **components/**: Reusable UI components
- **pages/**: Page-specific styles
- **utils/**: Animations, helpers

All CSS is imported through `main.css` in the correct order.

## 🧩 JavaScript Architecture

### Design Patterns

#### 1. **Strategy Pattern** (Sorting)
```javascript
// Different sorting algorithms as strategies
const sortStrategies = {
  alphaAsc: (a, b) => a.name.localeCompare(b.name),
  alphaDesc: (a, b) => b.name.localeCompare(a.name),
  // ... more strategies
};
```

#### 2. **Observer Pattern** (Event Bus)
```javascript
// Decoupled communication between modules
eventBus.on('LINK_CLICKED', (data) => {
  analytics.track('link_click', data);
});
```

#### 3. **Web Components**
```javascript
// Reusable custom elements
class SiteHeader extends HTMLElement {
  connectedCallback() {
    this.render();
  }
}
customElements.define('site-header', SiteHeader);
```

### Module Organization

- **constants/**: Magic strings and configuration
- **core/**: Core utilities (event bus, analytics)
- **features/**: Feature modules (sorting, rendering, tracking)
- **components/**: Web Components

## 📊 Data Structure

Applications are stored in `data/apps.json`:

```json
{
  "apps": [
    {
      "id": 1,
      "name": "App Name",
      "description": "Description",
      "category": "ai|games|productivity|creative",
      "status": "active|disabled|broken",
      "technologies": ["Azure", "Blazor", "..."],
      "url": "https://..."
    }
  ]
}
```

## 🔧 Configuration Files

### vite.config.js
Multi-page application configuration with all HTML entry points.

### package.json
- Scripts: `dev`, `build`, `preview`
- Type: `module` (ES6 modules)
- Dependencies: Vite 7.2.2

## 🎯 Features

- ✅ **Mobile-First Design**: Responsive across all devices
- ✅ **Modern Card Layout**: CSS Grid-based app cards
- ✅ **Status Badges**: Visual indicators (active/disabled/broken)
- ✅ **Dynamic Sorting**: Multiple sort options with animations
- ✅ **Staggered Animations**: Progressive card appearance
- ✅ **Web Components**: Reusable header/footer
- ✅ **Data-Driven**: JSON-based content management
- ✅ **Analytics Integration**: Application Insights tracking
- ✅ **Modular CSS**: Organized, maintainable styles
- ✅ **Build Optimization**: Vite for fast builds

## 📈 Application Insights

Analytics tracking is decoupled via Event Bus:

```javascript
// Track events anywhere in the app
eventBus.emit('LINK_CLICKED', { url, text });

// Analytics module listens and tracks
eventBus.on('LINK_CLICKED', (data) => {
  appInsights.trackEvent('link_click', data);
});
```

## 🎨 Design System

### Colors
Defined in `css/core/variables.css`:
- Primary: `#00c6ff` (Blue)
- Secondary: `#0072ff` (Dark Blue)
- Accent: `#32ffa7` (Green)
- Dark BG: `#1a1b26`
- Light BG: `#24253a`

### Typography
- System fonts: `system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif`
- Responsive sizing with clamp()

### Spacing
- Padding: `clamp(10px, 2vw, 20px)` for responsive scaling

## 🐛 Debugging

### Common Issues

1. **Module Import Errors**: Ensure all script tags use `type="module"`
2. **CSS Not Loading**: Check import paths in `main.css`
3. **Web Components Not Rendering**: Verify custom elements are registered before use

### Browser Console

Check for:
- Event Bus emissions: `eventBus.emit('EVENT_NAME', data)`
- Analytics tracking: Application Insights traces
- Rendering errors: Check console for JSON fetch errors

## 📝 Code Health Improvements

This project implements the following code health improvements:

1. ✅ **CSS Modularization**: Split 1090-line CSS into 13 focused modules
2. ✅ **Web Components**: Eliminated header/footer duplication across 6 pages
3. ✅ **Data-Driven Rendering**: Apps.json as single source of truth
4. ✅ **Strategy Pattern**: Extensible sorting algorithms
5. ✅ **SRP Modules**: Each module has single responsibility
6. ✅ **Event Bus**: Decoupled component communication
7. ✅ **Vite Build System**: Modern development workflow
8. ✅ **Constants File**: No magic strings

## 🔗 Links

- **Production Site**: https://popunkoutersoftware.azurewebsites.net
- **Contact**: punkouter24@gmail.com

## 📄 License

© 2025 Punkouter Software. All rights reserved.

---

## 🚀 Quick Commands

```bash
# Development
npm run dev          # Start dev server

# Production
npm run build        # Build for production
npm run preview      # Preview production build

# Deployment
# Upload contents of dist/ to Azure Static Web Apps
```

## 🎓 Learning Resources

- [Vite Documentation](https://vite.dev)
- [Web Components](https://developer.mozilla.org/en-US/docs/Web/Web_Components)
- [ES6 Modules](https://developer.mozilla.org/en-US/docs/Web/JavaScript/Guide/Modules)
- [Design Patterns](https://refactoring.guru/design-patterns)
