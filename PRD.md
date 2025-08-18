# Product Requirements Document (PRD)
## PoPunkouterSoftware Corporate Website

### Application Overview

**Project Name:** PoPunkouterSoftware Corporate Website  
**Version:** 1.0  
**Last Updated:** August 18, 2025  
**Document Owner:** Punkouter Software Team  

#### Purpose and Vision
PoPunkouterSoftware is a corporate website designed to showcase Punkouter Software's revolutionary AI-driven applications and diverse team. The website serves as the primary digital presence for the company, highlighting their commitment to innovation, diversity, and inclusive technology solutions.

#### Target Audience
- **Primary:** Potential clients seeking innovative software solutions
- **Secondary:** Developers and tech professionals interested in AI-driven applications
- **Tertiary:** Investors and partners looking for cutting-edge technology companies

#### Core Business Objectives
1. **Brand Presence:** Establish Punkouter Software as a leader in revolutionary software solutions
2. **Product Showcase:** Display the company's web and mobile applications portfolio
3. **Team Credibility:** Highlight the diverse and innovative team behind the solutions
4. **Lead Generation:** Provide easy contact methods for potential clients and partners
5. **SEO Optimization:** Ensure high visibility in search engines for relevant keywords

#### Key Features
- Responsive design optimized for all devices
- Modern, tech-focused aesthetic with animated elements
- Portfolio showcase of web and mobile applications
- Team member profiles and company culture representation
- Contact forms and communication channels
- SEO-optimized content and metadata
- Accessibility compliance (ARIA labels, semantic HTML)

#### Technical Architecture
- **Frontend:** Static HTML5, CSS3, and JavaScript
- **Styling:** Modern CSS with CSS custom properties and responsive design
- **Interactions:** Vanilla JavaScript for dynamic content loading and animations
- **Deployment:** Static web hosting ready for Azure Static Web Apps
- **Build System:** .NET tooling support for development workflow

#### Success Metrics
- Page load times under 3 seconds
- Mobile responsiveness across all major devices
- SEO score above 90/100
- Accessibility compliance (WCAG 2.1 AA)
- User engagement metrics (time on site, page views)

---

### UI Pages & Components

#### Page Structure Overview
The website follows a multi-page architecture with shared navigation and consistent styling across all pages.

#### 1. **Home Page (index.html)**
**Purpose:** Primary landing page introducing Punkouter Software and its mission

**Components:**
- **Header Component:**
  - Company logo (60px responsive)
  - Dynamic navigation menu (loaded via JavaScript)
  - Tech accent line with gradient animation
  
- **Gallery Section:**
  - Rotating showcase images (1.png, 2.png)
  - Lazy loading implementation
  - Alt text for accessibility
  
- **Introduction Section:**
  - Welcome heading (H1)
  - Company mission and vision statements
  - Call-to-action content emphasizing innovation and diversity
  
- **Footer Component:**
  - Copyright information
  - Consistent branding

**Technical Features:**
- Image rotation animation
- SEO meta tags and Open Graph properties
- Canonical URL specification
- Loading performance optimization

#### 2. **Our Team Page (OurTeam.html)**
**Purpose:** Showcase team members and company culture

**Components:**
- **Team Member Profiles:**
  - Matthew Herb - CEO profile with comprehensive background
  - Professional descriptions emphasizing leadership and vision
  - Expandable content sections
  
**Content Strategy:**
- Emphasis on visionary leadership
- Company culture and values representation
- Professional expertise highlighting

#### 3. **Web Apps Portfolio (OurWebApps.html)**
**Purpose:** Display web application portfolio with live links

**Components:**
- **App Cards Grid Layout:**
  - **PoAoeUsers:** AOE4 statistics tracking application
    - Live link: https://poaoeusers.azurewebsites.net/
    - Technologies: ASP.NET Core, Blazor Server, SQLite, Entity Framework, Bootstrap
    - Release year: 2024
  
  - **PoBabyNames:** Collaborative baby naming application
    - Live link: https://pobabynames.azurewebsites.net/
    - Technologies: ASP.NET Core, Blazor Server, SQLite, Entity Framework, Bootstrap
    - Release year: 2024
  
  - **Additional Applications:** (Expandable for future projects)

**Card Components:**
- Application title with external link
- Release year badge
- Feature description
- Technology stack tags
- Responsive grid layout

#### 4. **Phone Apps Portfolio (OurPhoneApps.html)**
**Purpose:** Showcase mobile applications available on App Store

**Components:**
- **Mobile App Cards:**
  - **PoReflexSquares:** Reflex testing game
    - App Store link: https://apps.apple.com/sn/app/poreflexsquares/id6479471889
    - Technologies: Unity, C#, iOS SDK
    - Release year: 2024
  
  - **PoThrowRagdoll:** Physics-based entertainment game
    - App Store link: https://apps.apple.com/us/app/pothrowragdoll/id6497065911
    - Technologies: Unity, C#, Physics Engine
    - Release year: 2024

#### 5. **Contact Page (Contact.html)**
**Purpose:** Provide contact information and communication channels

**Components:**
- Contact forms
- Business information
- Communication preferences
- Professional contact methods

#### Shared Components

##### **Navigation Component (navigation.html)**
**Dynamic Loading:** Loaded via JavaScript fetch API
**Structure:**
```html
- Home (Index.html)
- Our Team (OurTeam.html)  
- Web Apps (OurWebApps.html)
- Phone Apps (OurPhoneApps.html)
- Contact (Contact.html)
```

**Features:**
- Responsive design with flex layout
- ARIA accessibility labels
- Current page indication
- Error handling and retry functionality

##### **Styling System (style.css)**
**CSS Custom Properties:**
- Primary color: #00c6ff (Cyan blue)
- Secondary color: #0072ff (Deep blue)
- Accent color: #32ffa7 (Tech green)
- Dark background: #1a1b26
- Light background: #24253a

**Design Features:**
- Circuit board background pattern
- Modern CSS reset
- Responsive typography with clamp()
- Tech-focused gradient effects
- Mobile-first responsive design

##### **JavaScript Functionality (script.js)**
**Core Features:**
- Dynamic navigation loading
- Image rotation for gallery
- Error handling and user feedback
- Accessibility enhancements
- Performance monitoring hooks

**Technical Implementation:**
- Vanilla JavaScript (no frameworks)
- Modern ES6+ features
- Fetch API for content loading
- DOM manipulation and event handling
- Graceful error recovery

#### Design System Guidelines

**Typography:**
- System fonts: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif
- Responsive sizing using clamp() functions
- High contrast for accessibility

**Color Scheme:**
- Dark theme with tech-focused accent colors
- High contrast ratios for WCAG compliance
- Gradient effects for modern appearance

**Layout Principles:**
- Mobile-first responsive design
- Flexible grid systems
- Consistent spacing using custom properties
- Performance-optimized images and assets

**Accessibility Standards:**
- ARIA labels and semantic HTML
- Keyboard navigation support
- Screen reader compatibility
- Alt text for all images
- Focus management and visual indicators

#### Future Enhancement Opportunities
1. **Content Management:** Integration with headless CMS
2. **Internationalization:** Multi-language support
3. **Performance:** Progressive Web App features
4. **Analytics:** Enhanced user behavior tracking
5. **SEO:** Structured data markup implementation
6. **Security:** Content Security Policy headers
