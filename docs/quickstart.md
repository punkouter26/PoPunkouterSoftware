# Quick Start Guide

Simple diagrams for understanding the project structure.

## How It Works

```mermaid
graph LR
    A[Browser] --> B[HTML Page]
    B --> C[CSS Styling]
    B --> D[JavaScript]
    D --> E[JSON Data]
```

## Page Structure

```mermaid
graph TD
    A[Every Page Has]
    A --> B[site-header]
    A --> C[main content]
    A --> D[site-footer]
```

## File Organization

```mermaid
graph TD
    ROOT[wwwroot/] --> HTML[*.html files]
    ROOT --> CSS[css/ folder]
    ROOT --> JS[js/ folder]
    ROOT --> DATA[data/ folder]
    ROOT --> IMG[images/ folder]
```

## Making Changes

| What to Change | Where to Edit |
|----------------|---------------|
| Page content | `*.html` files |
| Colors/styles | `css/core/variables.css` |
| Header/Footer | `js/components/site-components.js` |
| App list | `data/apps.json` |

## Deployment

```mermaid
graph LR
    A[Edit Code] --> B[Git Push]
    B --> C[Auto Deploy]
    C --> D[Live on Azure]
```

That's it! No build tools, no npm, just simple web files.
