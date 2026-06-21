const publicLinks = [
    { href: "index.html", label: "Home" },
    { href: "deployment-paths.html", label: "Deployment Paths" },
    { href: "getting-started.html", label: "Prebuilt Setup" },
    { href: "features.html", label: "Features" },
    { href: "certification.html", label: "OIDC Self-Certification" },
    { href: "services.html", label: "Services" },
    { href: "about.html", label: "About" },
    { href: "contact.html", label: "Contact" },
    { href: "portal.html", label: "Customer Portal" }
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
    const isActive = activePath === link.href.toLowerCase();
    const activeClass = isActive ? " active" : "";
    const ariaCurrent = isActive ? ' aria-current="page"' : "";
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
            <button class="navbar-toggler" type="button" data-bs-toggle="collapse" data-bs-target="#navbarNav" aria-label="Toggle navigation" title="Toggle navigation">
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