# Architecture Documentation

## System Overview

```mermaid
graph TB
    subgraph "User Browser"
        HTML[HTML Pages]
        WC[Web Components]
        CSS[CSS Modules]
        JS[JS Modules]
    end
    
    subgraph "Static Assets"
        DATA[apps.json]
        IMG[images/]
    end
    
    subgraph "Azure"
        SWA[Static Web App]
        CDN[Azure CDN]
    end
    
    HTML --> WC
    HTML --> CSS
    HTML --> JS
    JS --> DATA
    HTML --> IMG
    
    SWA --> CDN
    CDN --> HTML
```

## Page Flow

```mermaid
graph LR
    HOME[index.html] --> TEAM[OurTeam.html]
    HOME --> WEBAPPS[OurWebApps.html]
    HOME --> PHONEAPPS[OurPhoneApps.html]
    HOME --> CONTACT[Contact.html]
    
    TEAM --> HOME
    WEBAPPS --> HOME
    PHONEAPPS --> HOME
    CONTACT --> HOME
    
    style HOME fill:#00c6ff
    style WEBAPPS fill:#0072ff
```

## JavaScript Module Dependencies

```mermaid
graph TD
    MAIN[main.js] --> COMPONENTS[site-components.js]
    MAIN --> ANALYTICS[analytics.js]
    MAIN --> SORTER[app-sorter.js]
    MAIN --> RENDERER[app-card-renderer.js]
    MAIN --> TRACKER[link-tracker.js]
    
    SORTER --> STRATEGIES[sort-strategies.js]
    SORTER --> EVENTBUS[event-bus.js]
    RENDERER --> CONSTANTS[app-constants.js]
    TRACKER --> EVENTBUS
    
    style MAIN fill:#00c6ff
    style EVENTBUS fill:#ff6b6b
```

## CSS Module Architecture

```mermaid
graph TD
    MAINCSS[main.css] --> CORE[core/]
    MAINCSS --> LAYOUT[layout/]
    MAINCSS --> COMPONENTS[components/]
    MAINCSS --> PAGES[pages/]
    MAINCSS --> UTILS[utils/]
    
    CORE --> VARIABLES[variables.css]
    CORE --> RESET[reset.css]
    
    LAYOUT --> HEADER[header.css]
    LAYOUT --> FOOTER[footer.css]
    LAYOUT --> LAYOUTMAIN[main.css]
    
    COMPONENTS --> CARDS[cards.css]
    COMPONENTS --> BADGES[badges.css]
    COMPONENTS --> BUTTONS[buttons.css]
    COMPONENTS --> CONTROLS[controls.css]
    COMPONENTS --> GALLERY[gallery.css]
    
    PAGES --> HOMECSS[home.css]
    PAGES --> TEAMCSS[team.css]
    PAGES --> CONTACTCSS[contact.css]
    
    UTILS --> ANIMATIONS[animations.css]
    
    style MAINCSS fill:#00c6ff
```

## Web Components

```mermaid
classDiagram
    class SiteHeader {
        +connectedCallback()
        +render()
        +highlightCurrentPage()
        +getCurrentPageName()
    }
    
    class SiteFooter {
        +connectedCallback()
        +render()
    }
    
    HTMLElement <|-- SiteHeader
    HTMLElement <|-- SiteFooter
```

## Event Bus Pattern

```mermaid
sequenceDiagram
    participant UI as Sort Dropdown
    participant Sorter as AppSorter
    participant Bus as EventBus
    participant Analytics as Analytics
    
    UI->>Sorter: onChange(sortType)
    Sorter->>Sorter: sortCards()
    Sorter->>Bus: emit('sort:changed', data)
    Bus->>Analytics: notify(data)
    Analytics->>Analytics: trackEvent()
```

## CI/CD Pipeline

```mermaid
graph LR
    DEV[Developer] --> |push| GH[GitHub master]
    GH --> |trigger| ACTION[GitHub Action]
    ACTION --> |deploy| SWA[Azure Static Web App]
    SWA --> |serve| USER[Users]
    
    style ACTION fill:#0072ff
    style SWA fill:#00c6ff
```

## Data Flow (OurWebApps Page)

```mermaid
flowchart LR
    JSON[(apps.json)] --> RENDERER[AppCardRenderer]
    RENDERER --> DOM[DOM Cards]
    
    SELECT[Sort Dropdown] --> SORTER[AppSorter]
    SORTER --> STRATEGY{Strategy}
    STRATEGY --> |alpha| ALPHA[alphaAsc/Desc]
    STRATEGY --> |status| STATUS[byStatus]
    STRATEGY --> |category| CAT[byCategory]
    
    SORTER --> DOM
```
