// Site-wide settings, kept in one place so links can be changed without touching the pages.
"use strict";

window.OBM_CONFIG = Object.freeze({
	// Where the Download buttons point. Update this when the repository is renamed.
	download_url: "https://github.com/psryland/one-blind-mouse/releases/latest",
});

// Point every element marked data-download at the download URL
document.addEventListener("DOMContentLoaded", () =>
{
	// The markup has no href of its own, so this is the only place the URL is defined
	for (const link of document.querySelectorAll("a[data-download]"))
		link.href = window.OBM_CONFIG.download_url;
});
