/**
 * Site Header Web Component
 * Reusable header with navigation
 */

class SiteHeader extends HTMLElement {
    connectedCallback() {
        this.render();
        this.highlightCurrentPage();
    }

    render() {
        this.innerHTML = `
            <header>
                <img src="images/logo.png" alt="Punkouter Software Logo" class="logo">
                <nav aria-label="Main navigation">
                    <ul class="nav-list">
                        <li><a href="index.html">Home</a></li>
                        <li><a href="OurTeam.html">Our Team</a></li>
                        <li><a href="OurWebApps.html">Web Apps</a></li>
                        <li><a href="OurPhoneApps.html">Phone Apps</a></li>
                        <li><a href="Contact.html">Contact</a></li>
                    </ul>
                </nav>
            </header>
        `;
    }

    highlightCurrentPage() {
        const currentPage = this.getCurrentPageName();
        const links = this.querySelectorAll('nav a');
        links.forEach(link => {
            if (link.getAttribute('href') === currentPage) {
                link.setAttribute('aria-current', 'page');
            }
        });
    }

    getCurrentPageName() {
        const parts = window.location.pathname.split('/').filter(Boolean);
        return parts.length > 0 ? parts[parts.length - 1] : 'index.html';
    }
}

/**
 * Site Footer Web Component
 * Reusable footer with dynamic copyright year
 */

class SiteFooter extends HTMLElement {
    connectedCallback() {
        this.render();
    }

    render() {
        const year = new Date().getFullYear();
        this.innerHTML = `
            <footer>
                <p>&copy; ${year} Punkouter Software. All rights reserved.</p>
            </footer>
        `;
    }
}

// Register custom elements
customElements.define('site-header', SiteHeader);
customElements.define('site-footer', SiteFooter);

export { SiteHeader, SiteFooter };
