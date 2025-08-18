# PoPunkouterSoftware

## Summary

**PoPunkouterSoftware** is a modern, responsive corporate website showcasing Punkouter Software's revolutionary AI-driven applications and diverse team. Built with modern web technologies, the site emphasizes innovation, diversity, and inclusive technology solutions while maintaining high performance and accessibility standards.

### Key Features
- 🎨 **Modern Design:** Tech-focused aesthetic with animated elements and gradient effects
- 📱 **Responsive Layout:** Optimized for all devices with mobile-first approach
- ⚡ **Performance Optimized:** Fast loading times with lazy loading and optimized assets
- ♿ **Accessibility Compliant:** WCAG 2.1 AA standards with ARIA labels and semantic HTML
- 🔍 **SEO Optimized:** Comprehensive meta tags, Open Graph, and structured content
- 🚀 **Portfolio Showcase:** Interactive displays of web and mobile applications

### Technology Stack
- **Frontend:** HTML5, CSS3, Vanilla JavaScript
- **Styling:** Modern CSS with custom properties and flexbox/grid
- **Build Tools:** .NET tooling support
- **Deployment:** Azure Static Web Apps ready

---

## Getting Started

### Prerequisites
- Modern web browser (Chrome, Firefox, Safari, Edge)
- Local web server for development (optional)
- .NET SDK (for build tasks) - optional

### Installation & Setup

#### Option 1: Simple Setup (Recommended for quick viewing)
1. **Clone or download the repository:**
   ```bash
   git clone https://github.com/punkouter25/PoPunkouterSoftware.git
   cd PoPunkouterSoftware
   ```

2. **Open the main index.html file:**
   - Navigate to the root directory
   - Open `index.html` in your web browser
   - The site will automatically redirect to `PoPunkouterSoftware/wwwroot/index.html`

#### Option 2: Local Development Server
1. **Using VS Code Live Server extension:**
   ```bash
   # Install VS Code Live Server extension
   # Right-click on index.html and select "Open with Live Server"
   ```

2. **Using Node.js live-server (if available):**
   ```bash
   npm install -g live-server
   live-server
   ```

3. **Using the included server.js (if Node.js is installed):**
   ```bash
   node server.js
   ```

#### Option 3: .NET Development Environment
1. **Using the built-in VS Code task:**
   ```bash
   # Open VS Code in the project directory
   # Press Ctrl+Shift+P (Cmd+Shift+P on Mac)
   # Type "Tasks: Run Task"
   # Select "start-live-server"
   ```

2. **Using .NET build tasks:**
   ```bash
   # Clean the project
   dotnet clean PoPunkouterSoftware

   # Build the project
   dotnet build PoPunkouterSoftware/PoPunkouterSoftware.csproj

   # Publish for release
   dotnet publish PoPunkouterSoftware --configuration Release
   ```

### Project Structure
```
PoPunkouterSoftware/
├── index.html                          # Root redirect page
├── server.js                          # Optional Node.js server
├── PRD.md                             # Product Requirements Document
├── README.md                          # This file
├── Diagrams/                          # Architecture and workflow diagrams
│   ├── Component.mmd                  # Component architecture
│   ├── DomainModel.mmd               # Domain model
│   ├── UserWorkflow.mmd              # User workflow
│   ├── Deployment.mmd                # Azure deployment
│   └── *.svg                         # SVG versions of diagrams
├── PoPunkouterSoftware/
│   ├── .config/
│   │   └── dotnet-tools.json         # .NET tooling configuration
│   └── wwwroot/                      # Web application root
│       ├── index.html                # Main landing page
│       ├── navigation.html           # Shared navigation component
│       ├── OurTeam.html             # Team showcase page
│       ├── OurWebApps.html          # Web applications portfolio
│       ├── OurPhoneApps.html        # Mobile applications portfolio
│       ├── Contact.html             # Contact information page
│       ├── PrivacyPolicy.html       # Privacy policy page
│       ├── css/
│       │   └── style.css            # Main stylesheet
│       ├── js/
│       │   └── script.js            # Main JavaScript functionality
│       └── images/
│           ├── logo.png             # Company logo
│           ├── favicon.ico          # Site favicon
│           ├── 1.png, 2.png        # Gallery images
│           ├── matt.png             # Team member photos
│           └── contact_man.webp     # Contact page assets
```

### Development Guidelines

#### Making Changes
1. **Content Updates:** Modify HTML files in `PoPunkouterSoftware/wwwroot/`
2. **Styling Changes:** Edit `PoPunkouterSoftware/wwwroot/css/style.css`
3. **Functionality Updates:** Modify `PoPunkouterSoftware/wwwroot/js/script.js`
4. **Images:** Add new images to `PoPunkouterSoftware/wwwroot/images/`

#### Testing Your Changes
1. **Local Testing:** Always test changes in a local development server
2. **Cross-Browser Testing:** Verify functionality in multiple browsers
3. **Mobile Testing:** Check responsive design on various screen sizes
4. **Accessibility Testing:** Use browser dev tools to verify accessibility

#### Performance Optimization
- **Images:** Optimize images before adding (WebP format recommended)
- **CSS:** Minimize unused styles and leverage CSS custom properties
- **JavaScript:** Keep scripts lightweight and avoid unnecessary dependencies

---

## Key Connections

### External Integrations

#### **Live Web Applications**
- **PoAoeUsers:** https://poaoeusers.azurewebsites.net/
  - *Purpose:* AOE4 game statistics tracking
  - *Technologies:* ASP.NET Core, Blazor Server, SQLite, Entity Framework
  - *Status:* Production deployment on Azure

- **PoBabyNames:** https://pobabynames.azurewebsites.net/
  - *Purpose:* Collaborative baby naming decision tool
  - *Technologies:* ASP.NET Core, Blazor Server, SQLite, Entity Framework
  - *Status:* Production deployment on Azure

#### **Mobile Applications**
- **PoReflexSquares:** https://apps.apple.com/sn/app/poreflexsquares/id6479471889
  - *Platform:* iOS App Store
  - *Technologies:* Unity, C#, iOS SDK
  - *Type:* Reflex testing game

- **PoThrowRagdoll:** https://apps.apple.com/us/app/pothrowragdoll/id6497065911
  - *Platform:* iOS App Store
  - *Technologies:* Unity, C#, Physics Engine
  - *Type:* Physics-based entertainment game

### Technical Dependencies

#### **Frontend Dependencies**
- **No External Frameworks:** Pure HTML5, CSS3, and Vanilla JavaScript
- **Font System:** System fonts for optimal performance
- **Image Formats:** PNG, WebP, ICO for various use cases

#### **Development Tools**
- **.NET SDK:** For build and development tasks
- **Entity Framework Tools:** Version 9.0.1 (configured in dotnet-tools.json)
- **VS Code Tasks:** Configured for development workflow

#### **Hosting & Deployment**
- **Azure Static Web Apps:** Recommended deployment target
- **Azure App Service:** For related web applications
- **CDN Integration:** For global content delivery optimization

### API Connections
*Currently no external APIs are integrated. Future enhancements may include:*
- **Analytics API:** For user behavior tracking
- **Contact Form API:** For lead generation
- **CMS API:** For dynamic content management

### Third-Party Services

#### **Social Media Integration**
- **Open Graph:** Facebook/LinkedIn sharing optimization
- **Twitter Cards:** Twitter sharing enhancement
- **App Store Connect:** iOS application distribution

#### **SEO & Analytics**
- **Google Search Console:** (Ready for integration)
- **Google Analytics:** (Hooks available in JavaScript)
- **Application Insights:** (Integration points available)

### Internal Connections

#### **Component Architecture**
- **Navigation Component:** Dynamically loaded across all pages
- **Shared Styling:** Centralized CSS custom properties system
- **Unified JavaScript:** Consistent functionality across pages

#### **Content Management**
- **Static Content:** HTML-based content management
- **Image Assets:** Centralized image directory structure
- **Configuration:** .NET tooling for development environment

### Security Considerations
- **HTTPS Ready:** Prepared for secure deployment
- **Content Security Policy:** Ready for CSP header implementation
- **Input Validation:** Prepared for contact form security
- **Asset Integrity:** Optimized for secure asset delivery

### Performance Monitoring
- **Core Web Vitals:** Optimized for Google's performance metrics
- **Loading Performance:** Lazy loading and optimized asset delivery
- **Runtime Performance:** Efficient JavaScript execution
- **Accessibility Performance:** WCAG 2.1 AA compliance tracking

### Future Expansion Points
- **CMS Integration:** Ready for headless CMS connection
- **Database Integration:** Prepared for dynamic content storage
- **API Development:** Architecture supports RESTful API integration
- **Mobile App Integration:** Ready for deep linking and app promotion
- **Internationalization:** Structure supports multi-language expansion
