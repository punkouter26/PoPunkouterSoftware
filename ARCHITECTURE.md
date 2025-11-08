# Architecture Diagrams

## System Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     USER BROWSER                            │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌───────────────┐  ┌───────────────┐  ┌───────────────┐ │
│  │  index.html   │  │OurWebApps.html│  │  Other Pages  │ │
│  └───────┬───────┘  └───────┬───────┘  └───────┬───────┘ │
│          │                  │                  │           │
│          └──────────────────┴──────────────────┘           │
│                          │                                 │
│          ┌───────────────▼────────────────┐               │
│          │      Web Components            │               │
│          │  <site-header> <site-footer>   │               │
│          └───────────────┬────────────────┘               │
│                          │                                 │
│          ┌───────────────▼────────────────┐               │
│          │        main.js (Entry)         │               │
│          │  - App Initialization          │               │
│          │  - Component Registration      │               │
│          └───────────────┬────────────────┘               │
│                          │                                 │
│       ┌──────────────────┼──────────────────┐            │
│       │                  │                  │             │
│   ┌───▼────┐      ┌──────▼──────┐    ┌────▼─────┐       │
│   │ Core   │      │  Features   │    │Components│       │
│   ├────────┤      ├─────────────┤    ├──────────┤       │
│   │EventBus│      │AppRenderer  │    │SiteHeader│       │
│   │Analytics│     │AppSorter    │    │SiteFooter│       │
│   └───┬────┘      │LinkTracker  │    └──────────┘       │
│       │           │Gallery      │                        │
│       │           └──────┬──────┘                        │
│       │                  │                                │
│       │           ┌──────▼──────┐                        │
│       │           │ Constants   │                        │
│       │           │ Strategies  │                        │
│       │           └─────────────┘                        │
│       │                                                   │
│   ┌───▼───────────────────┐                             │
│   │    Event Bus          │                             │
│   │  (Observer Pattern)   │                             │
│   └───────────────────────┘                             │
│                                                           │
│   ┌──────────────────────────────────────┐             │
│   │         CSS Modules                  │             │
│   │  main.css → core, layout, components │             │
│   └──────────────────────────────────────┘             │
│                                                           │
│   ┌──────────────────────────────────────┐             │
│   │         Data (JSON)                  │             │
│   │  apps.json → Application Data        │             │
│   └──────────────────────────────────────┘             │
└─────────────────────────────────────────────────────────────┘
```

## Module Dependency Graph

```
┌─────────────────────────────────────────────────────────┐
│                      main.js                            │
└─────┬───────────────────────────────────┬───────────────┘
      │                                   │
      ▼                                   ▼
┌─────────────────┐             ┌────────────────────┐
│  Components     │             │    Features        │
│  Module         │             │    Module          │
├─────────────────┤             ├────────────────────┤
│ SiteHeader      │             │ AppCardRenderer ──┐│
│ SiteFooter      │             │ AppSorter      ───┤│
└─────────────────┘             │ LinkTracker       ││
                                │ ImageGallery      ││
                                └───────────────────┬┘│
                                                    │ │
      ┌─────────────────────────────────────────────┘ │
      │                                               │
      ▼                                               ▼
┌─────────────────┐                       ┌──────────────────┐
│  Core Module    │                       │  Constants       │
├─────────────────┤                       ├──────────────────┤
│ EventBus        │◄──────────────────────│ SORT_TYPES       │
│ Analytics       │                       │ APP_STATUS       │
└─────────────────┘                       │ EVENT_TYPES      │
                                          │ CATEGORY_LABELS  │
                                          └──────────────────┘
                                                    ▲
      ┌─────────────────────────────────────────────┘
      │
      ▼
┌──────────────────┐
│  Strategies      │
├──────────────────┤
│ sortStrategies   │
│ - alphaAsc       │
│ - alphaDesc      │
│ - status         │
│ - category       │
└──────────────────┘
```

## Data Flow - Rendering App Cards

```
┌─────────────┐
│ User visits │
│OurWebApps   │
└──────┬──────┘
       │
       ▼
┌─────────────────┐
│   main.js       │
│ detectsAppsPage │
└──────┬──────────┘
       │
       ▼
┌──────────────────────┐
│ AppCardRenderer      │
│  .loadApps()         │
└──────┬───────────────┘
       │
       ▼
┌──────────────────────┐
│  Fetch apps.json     │
│  GET /data/apps.json │
└──────┬───────────────┘
       │
       ▼
┌──────────────────────┐
│  Parse JSON          │
│  { apps: [...] }     │
└──────┬───────────────┘
       │
       ▼
┌──────────────────────┐
│  For each app:       │
│  .createCard(app)    │
└──────┬───────────────┘
       │
       ▼
┌──────────────────────┐
│  Generate HTML       │
│  - Badge based on    │
│    status            │
│  - Category tag      │
│  - Tech badges       │
└──────┬───────────────┘
       │
       ▼
┌──────────────────────┐
│  Append to DOM       │
│  #apps-grid          │
└──────┬───────────────┘
       │
       ▼
┌──────────────────────┐
│  Emit event          │
│  APPS_RENDERED       │
└──────┬───────────────┘
       │
       ▼
┌──────────────────────┐
│  AppSorter listens   │
│  Attaches handlers   │
└──────────────────────┘
```

## Event Flow - Sorting Apps

```
┌─────────────────┐
│  User clicks    │
│  sort dropdown  │
└────────┬────────┘
         │
         ▼
┌─────────────────────┐
│  AppSorter          │
│  .handleSort()      │
└────────┬────────────┘
         │
         ▼
┌─────────────────────┐
│  Get strategy from  │
│  sortStrategies[key]│
└────────┬────────────┘
         │
         ▼
┌─────────────────────┐
│  Sort cards array   │
│  cards.sort(strategy)│
└────────┬────────────┘
         │
         ▼
┌─────────────────────┐
│  Animate reorder    │
│  .fadeOut()         │
└────────┬────────────┘
         │
         ▼
┌─────────────────────┐
│  Reorder DOM        │
│  appendChild()      │
└────────┬────────────┘
         │
         ▼
┌─────────────────────┐
│  Animate in         │
│  .fadeIn()          │
└────────┬────────────┘
         │
         ▼
┌─────────────────────┐
│  Emit event         │
│  APP_SORTED         │
└────────┬────────────┘
         │
         ▼
┌─────────────────────┐
│  Analytics tracks   │
│  sort action        │
└─────────────────────┘
```

## CSS Import Hierarchy

```
┌────────────────────────────────────────┐
│           main.css                     │
└┬───────────────────────────────────────┘
 │
 ├─► core/variables.css    (CSS vars)
 ├─► core/reset.css        (CSS reset)
 │
 ├─► layout/main.css       (base layout)
 ├─► layout/header.css     (header/nav)
 ├─► layout/footer.css     (footer)
 │
 ├─► components/cards.css  (app cards)
 ├─► components/badges.css (status badges)
 ├─► components/buttons.css (buttons)
 ├─► components/controls.css (sort controls)
 ├─► components/gallery.css (image gallery)
 │
 ├─► utils/animations.css  (keyframes)
 ├─► utils/legacy-table.css (old table)
 │
 ├─► pages/home.css        (homepage)
 ├─► pages/team.css        (team page)
 └─► pages/contact.css     (contact page)
```

## Build Process Flow

```
┌─────────────────┐
│   npm run dev   │
│  or             │
│  npm run build  │
└────────┬────────┘
         │
         ▼
┌─────────────────────┐
│   Vite CLI          │
└────────┬────────────┘
         │
         ▼
┌─────────────────────┐
│  Read vite.config.js│
│  - Entry points     │
│  - Build options    │
└────────┬────────────┘
         │
         ▼
┌─────────────────────┐
│  Process HTML files │
│  - Inject scripts   │
│  - Resolve imports  │
└────────┬────────────┘
         │
         ▼
┌─────────────────────┐
│  Bundle JavaScript  │
│  - Tree shaking     │
│  - Minification     │
│  - Code splitting   │
└────────┬────────────┘
         │
         ▼
┌─────────────────────┐
│  Process CSS        │
│  - Resolve imports  │
│  - Minification     │
│  - Autoprefixer     │
└────────┬────────────┘
         │
         ▼
┌─────────────────────┐
│  Copy static assets │
│  - Images           │
│  - Fonts            │
│  - Data files       │
└────────┬────────────┘
         │
         ▼
┌─────────────────────┐
│  Output to dist/    │
│  - Optimized files  │
│  - Source maps      │
└─────────────────────┘
```

## Design Pattern Implementation

### Strategy Pattern (Sorting)
```
┌──────────────────────────────┐
│     AppSorter (Context)      │
│                              │
│  - setStrategy(strategy)     │
│  - executeSort()             │
└───────────┬──────────────────┘
            │ uses
            ▼
┌───────────────────────────────┐
│    sortStrategies (Map)       │
├───────────────────────────────┤
│  alphaAsc:  ┌──────────────┐ │
│             │ Sort A→Z     │ │
│             └──────────────┘ │
│  alphaDesc: ┌──────────────┐ │
│             │ Sort Z→A     │ │
│             └──────────────┘ │
│  status:    ┌──────────────┐ │
│             │ Sort by      │ │
│             │ status       │ │
│             └──────────────┘ │
│  category:  ┌──────────────┐ │
│             │ Sort by      │ │
│             │ category     │ │
│             └──────────────┘ │
└───────────────────────────────┘
```

### Observer Pattern (Event Bus)
```
┌─────────────────────────────────────┐
│         EventBus (Subject)          │
├─────────────────────────────────────┤
│  events: Map<string, Function[]>    │
│                                     │
│  on(event, callback)                │
│  emit(event, data)                  │
│  off(event, callback)               │
└──────────┬─────────────┬────────────┘
           │             │
    ┌──────▼─────┐ ┌────▼──────┐
    │ Observer 1 │ │ Observer 2│
    │ (Analytics)│ │ (Logger)  │
    └────────────┘ └───────────┘
    
When event emitted:
  eventBus.emit('LINK_CLICKED', data)
    │
    ├──► analytics.track(data)
    └──► logger.log(data)
```

### Web Components (Custom Elements)
```
┌────────────────────────────────────┐
│      HTMLElement (Base Class)      │
└────────────┬───────────────────────┘
             │ extends
        ┌────┴────┐
        │         │
┌───────▼─────┐ ┌▼────────────┐
│ SiteHeader  │ │ SiteFooter  │
├─────────────┤ ├─────────────┤
│ - render()  │ │ - render()  │
│ - highlight │ │             │
└─────────────┘ └─────────────┘
        │
        ▼
Custom element registration:
  customElements.define('site-header', SiteHeader)

Usage in HTML:
  <site-header current-page="index.html"></site-header>
```

## File Size Comparison

### Before Refactoring
```
style.css:            1090 lines  (~35 KB)
script.js:             100 lines  (~3 KB)
index.html:             65 lines  (~2 KB)
OurWebApps.html:       937 lines  (~30 KB)
OurTeam.html:           50 lines  (~1.5 KB)
Contact.html:           45 lines  (~1.3 KB)
OurPhoneApps.html:      40 lines  (~1.2 KB)
PrivacyPolicy.html:    170 lines  (~6 KB)
────────────────────────────────────────
Total:                2497 lines  (~80 KB)
```

### After Refactoring
```
CSS (13 files):       1090 lines  (~35 KB) - modularized
JS (12 modules):       450 lines  (~15 KB) - organized
HTML (6 files):        250 lines  (~8 KB)  - simplified
Data (1 JSON):          80 lines  (~2 KB)  - structured
Config (2 files):       50 lines  (~2 KB)  - build system
────────────────────────────────────────
Total:                1920 lines  (~62 KB) - 23% reduction

Build output (dist/):
  - Minified JS:       ~8 KB (gzipped: ~3 KB)
  - Minified CSS:     ~12 KB (gzipped: ~4 KB)
  - HTML:              ~8 KB (gzipped: ~3 KB)
────────────────────────────────────────
Production total:     ~28 KB (gzipped: ~10 KB)
```
