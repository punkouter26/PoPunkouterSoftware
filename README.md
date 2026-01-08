# Punkouter Software Website

Simple, modular static website built with vanilla HTML, CSS, and ES6 JavaScript.

Live Site: https://agreeable-forest-03be55f0f.4.azurestaticapps.net

## Architecture Overview

A pure static website with no build tools required:

- **No Build System**: Just HTML, CSS, and JavaScript
- **ES6 Modules**: Native browser module support
- **Web Components**: Reusable `<site-header>` and `<site-footer>`
- **Data-Driven**: JSON-based app rendering
- **Design Patterns**: Strategy, Observer (Event Bus), Module patterns
- **CSS Architecture**: Modular CSS with organized imports

## Project Structure

```
PoPunkouterSoftware/
├── .github/workflows/         # CI/CD
│   └── azure-static-web-apps.yml
├── PoPunkouterSoftware/
│   └── wwwroot/              # Static site root
│       ├── index.html        # Homepage
│       ├── OurWebApps.html   # Web apps showcase
│       ├── OurTeam.html      # Team page
│       ├── Contact.html      # Contact page
│       ├── OurPhoneApps.html # Phone apps page
│       ├── PrivacyPolicy.html
│       ├── css/              # Modular CSS (12 files)
│       ├── js/               # ES6 Modules (8 files)
│       ├── data/apps.json    # App data
│       └── images/           # Static assets
├── docs/                     # Documentation
└── README.md
```

## Getting Started

### Run Locally

No installation required! Just open the HTML files or use any static server:

```bash
# Option 1: Open directly in browser
start PoPunkouterSoftware/wwwroot/index.html

# Option 2: VS Code Live Server extension
# Right-click index.html -> Open with Live Server
```

### Deployment

The site auto-deploys to Azure Static Web Apps on push to master branch.

| Environment | URL |
|-------------|-----|
| Production | https://agreeable-forest-03be55f0f.4.azurestaticapps.net |

## Pages

| Page | Description |
|------|-------------|
| index.html | Homepage with company intro |
| OurWebApps.html | Web applications showcase with sorting |
| OurTeam.html | Team member profiles |
| OurPhoneApps.html | Mobile apps (coming soon) |
| Contact.html | Contact information |
| PrivacyPolicy.html | Privacy policy |

## CSS Architecture

```
css/
├── main.css          # Imports all modules
├── core/             # Variables, reset
├── layout/           # Header, footer, main
├── components/       # Cards, badges, buttons
├── pages/            # Page-specific styles
└── utils/            # Animations
```

## JavaScript Modules

| Module | Purpose |
|--------|---------|
| main.js | Entry point, app initialization |
| site-components.js | Web Components (header/footer) |
| app-card-renderer.js | Renders app cards from JSON |
| app-sorter.js | Sorts app cards |
| sort-strategies.js | Strategy pattern for sorting |
| event-bus.js | Observer pattern for decoupling |
| analytics.js | App Insights wrapper |
| link-tracker.js | Link click tracking |

## Contributing

1. Fork the repository
2. Create a feature branch
3. Commit changes
4. Push to branch
5. Open a Pull Request

## Author

Punkouter Software - punkouter24@gmail.com
