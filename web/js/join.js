// Join page: shows the session code from a /j/<CODE> link and offers to open it in the app.
"use strict";

// Code rules, matching SessionCode in the app: Crockford base32, 8 characters, '-' and ' ' ignored, O→0 and I/L→1.
const ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
const CODE_LENGTH = 8;

// Return the canonical 8-character code for 'text', or null if it is not a valid code
function parse_code(text)
{
	// Reject missing or unreasonably long input
	if (typeof text !== "string" || text.length === 0 || text.length > 64)
		return null;

	// Drop separators, apply the aliases, and accept only the canonical alphabet
	let code = "";
	for (const ch of text.toUpperCase())
	{
		// Separators may appear anywhere
		if (ch === "-" || ch === " ")
			continue;

		const c = ch === "O" ? "0" : (ch === "I" || ch === "L") ? "1" : ch;
		if (!ALPHABET.includes(c) || code.length === CODE_LENGTH)
			return null;

		code += c;
	}
	return code.length === CODE_LENGTH ? code : null;
}

// Read the code from the path, e.g. /j/K7QM-4PZX, or return null if there is no valid code
function code_from_location()
{
	// Expect exactly "/j/<code>"
	const segments = window.location.pathname.split("/").filter(s => s.length !== 0);
	if (segments.length !== 2 || segments[0].toLowerCase() !== "j")
		return null;

	// Malformed percent-encoding just means there is no valid code
	try
	{
		return parse_code(decodeURIComponent(segments[1]));
	}
	catch
	{
		return null;
	}
}

// Fill in the page once it has loaded
document.addEventListener("DOMContentLoaded", () =>
{
	// Show either the code panel or the invalid-code message
	const code = code_from_location();
	document.getElementById("valid").hidden = code === null;
	document.getElementById("invalid").hidden = code !== null;
	if (code === null)
		return;

	// Display the grouped form, and link to the app with the compact form. The user clicks the link; the page never redirects.
	const display = `${code.slice(0, 4)}-${code.slice(4)}`;
	document.getElementById("code").textContent = display;
	document.getElementById("open").href = `obm://join/${code}`;
	document.title = `Join ${display} — One-Blind-Mouse`;

	// Copy the grouped code, reporting the result in a polite live region
	const status = document.getElementById("copy-status");
	document.getElementById("copy").addEventListener("click", async () =>
	{
		// The clipboard API needs a secure context and permission; report failure rather than throwing
		try
		{
			await navigator.clipboard.writeText(display);
			status.textContent = "Copied";
		}
		catch
		{
			status.textContent = "Copy failed. Select the code and copy it manually.";
		}
	});
});
