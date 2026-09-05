const publicLinks = [
    { href: "features.html", label: "Features" },
    { href: "deployment-paths.html", label: "Install" },
    { href: "certification.html", label: "Conformance" },
    { href: "services.html", label: "Services" },
    { href: "about.html", label: "About" },
    { href: "contact.html", label: "Contact" },
    { href: "portal.html", label: "Customer portal" }
];

const compactLinks = [
    { href: "index.html", label: "Home" },
    { href: "portal.html", label: "Customer Portal" }
];

function getCurrentPage() {
    const currentPath = window.location.pathname.split("/").pop();
    return (currentPath || "index.html").toLowerCase();
}

function renderLink(link, activePath, wrapInListItem = true) {
    const isInstallationPage = ["getting-started.html", "advanced-source-build.html"].includes(activePath);
    const isActive = activePath === link.href.toLowerCase()
        || (link.href === "deployment-paths.html" && isInstallationPage);
    const activeClass = isActive ? " active" : "";
    const ariaCurrent = isActive ? ` aria-current="${activePath === link.href.toLowerCase() ? "page" : "location"}"` : "";
    const anchorMarkup = `<a class="nav-link${activeClass}" href="${link.href}"${ariaCurrent}>${link.label}</a>`;

    return wrapInListItem ? `<li class="nav-item">${anchorMarkup}</li>` : anchorMarkup;
}

function renderPublicNav(activePath) {
    const linksMarkup = publicLinks.map((link) => renderLink(link, activePath)).join("");

    return `
        <div class="container">
            <a class="navbar-brand fw-bold" href="index.html">
                <i class="bi bi-shield-lock me-2"></i>MrWhoOidc
            </a>
            <button class="navbar-toggler" type="button" data-bs-toggle="collapse" data-bs-target="#navbarNav" aria-controls="navbarNav" aria-expanded="false" aria-label="Toggle navigation" title="Toggle navigation">
                <span class="navbar-toggler-icon"></span>
            </button>
            <div class="collapse navbar-collapse" id="navbarNav">
                <ul class="navbar-nav ms-auto">
                    ${linksMarkup}
                </ul>
            </div>
        </div>`;
}

function renderCompactNav() {
    const linksMarkup = compactLinks.map((link) => renderLink(link, "", false)).join("");

    return `
        <div class="container">
            <a class="navbar-brand fw-bold" href="index.html">
                <i class="bi bi-shield-lock me-2"></i>MrWhoOidc
            </a>
            <div class="navbar-nav ms-auto">
                ${linksMarkup}
            </div>
        </div>`;
}

document.querySelectorAll("nav[data-site-nav]").forEach((navElement) => {
    const mode = navElement.dataset.siteNav === "compact" ? "compact" : "public";

    navElement.className = "navbar navbar-expand-lg navbar-dark site-nav";
    navElement.innerHTML = mode === "compact"
        ? renderCompactNav()
        : renderPublicNav(getCurrentPage());
});