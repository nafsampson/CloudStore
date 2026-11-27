//summary//
// Provides accessible catalog search/sort, form feedback and image fallbacks without replacing server validation.
//summary//
//-------------------Start Of File------------------//
"use strict";
(() => {
    const navigationToggle = document.querySelector("[data-navigation-toggle]");
    const mobileNavigation = document.getElementById("mobile-navigation");
    const closeNavigation = () => {
        if (!mobileNavigation || !navigationToggle) return;
        mobileNavigation.hidden = true;
        navigationToggle.setAttribute("aria-expanded", "false");
    };
    navigationToggle?.addEventListener("click", () => {
        mobileNavigation.hidden = !mobileNavigation.hidden;
        navigationToggle.setAttribute("aria-expanded", String(!mobileNavigation.hidden));
    });
    document.addEventListener("keydown", event => {
        if (event.key === "Escape" && mobileNavigation && !mobileNavigation.hidden) {
            closeNavigation(); navigationToggle.focus();
        }
    });
    matchMedia("(min-width:1024px)").addEventListener("change", event => { if (event.matches) closeNavigation(); });
    document.querySelectorAll("[data-password-toggle]").forEach(button => {
        button.addEventListener("click", () => {
            const input = document.getElementById(button.dataset.passwordToggle);
            const visible = input.type === "password";
            input.type = visible ? "text" : "password";
            button.textContent = visible ? "Hide" : "Show";
            button.setAttribute("aria-label", visible ? "Hide password" : "Show password");
            button.setAttribute("aria-pressed", String(visible));
        });
    });
    document.querySelectorAll("[data-open-panel]").forEach(button => {
        button.addEventListener("click", () => {
            const panel = document.getElementById(button.dataset.openPanel);
            if (!panel) return;
            panel.open = true;
            panel.scrollIntoView({ behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth", block: "center" });
            panel.querySelector("input:not([type=hidden])")?.focus({ preventScroll: true });
        });
    });
    document.querySelectorAll("[data-catalog]").forEach(catalog => {
        const search = catalog.querySelector("[data-catalog-search]");
        const sort = catalog.querySelector("[data-catalog-sort]");
        const grid = catalog.querySelector("[data-catalog-grid]");
        const cards = Array.from(grid.querySelectorAll("[data-product-card]"));
        const update = () => {
            const query = search.value.trim().toLocaleLowerCase();
            let visible = 0;
            cards.forEach(card => {
                card.hidden = !card.dataset.search.toLocaleLowerCase().includes(query);
                if (!card.hidden) visible++;
            });
            const ordered = [...cards];
            if (sort.value === "name") ordered.sort((a, b) => a.querySelector("h2").textContent.localeCompare(b.querySelector("h2").textContent));
            if (sort.value.startsWith("price")) ordered.sort((a, b) => {
                const first = a.dataset.price === "" ? null : Number(a.dataset.price);
                const second = b.dataset.price === "" ? null : Number(b.dataset.price);
                if (first === null) return second === null ? 0 : 1;
                if (second === null) return -1;
                return (first - second) * (sort.value === "price-high" ? -1 : 1);
            });
            ordered.forEach(card => grid.appendChild(card));
            catalog.querySelector("[data-catalog-count]").textContent = `${visible} ${visible === 1 ? "product" : "products"}`;
            catalog.querySelector("[data-catalog-empty]").hidden = visible !== 0 || cards.length === 0;
        };
        search.addEventListener("input", update);
        sort.addEventListener("change", update);
        search.value = new URLSearchParams(window.location.search).get("search")?.slice(0, 128) ?? "";
        update();
        catalog.querySelector("[data-catalog-reset]").addEventListener("click", () => { search.value = ""; update(); search.focus(); });
    });
    document.querySelectorAll("[data-product-image]").forEach(image => {
        const fallback = () => { image.hidden = true; };
        const loaded = () => { if (image.naturalWidth < 8 || image.naturalHeight < 8) fallback(); };
        image.addEventListener("error", fallback);
        image.addEventListener("load", loaded);
        if (image.complete) { if (image.naturalWidth === 0) fallback(); else loaded(); }
    });
    document.querySelectorAll("form[method=post]").forEach(form => {
        form.addEventListener("submit", event => {
            if (form.dataset.confirm && !window.confirm(form.dataset.confirm)) { event.preventDefault(); return; }
            if (event.defaultPrevented || !form.checkValidity()) return;
            form.querySelectorAll("button[type=submit],button:not([type])").forEach(button => {
                button.disabled = true;
                button.setAttribute("aria-busy", "true");
            });
        });
    });
    window.addEventListener("pageshow", () => document.querySelectorAll("button[aria-busy=true]").forEach(button => {
        button.disabled = false;
        button.removeAttribute("aria-busy");
    }));
})();
//-------------------End Of File------------------//
