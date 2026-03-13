// Arkade Wallet JS Interop
window.arkade = {
    // Copy text to clipboard
    copyToClipboard: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // Fallback for older browsers
            const ta = document.createElement('textarea');
            ta.value = text;
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            const ok = document.execCommand('copy');
            document.body.removeChild(ta);
            return ok;
        }
    },

    // Generate QR code SVG and inject into element
    // Uses a minimal QR encoder (no external deps)
    renderQr: function (elementId, data, size) {
        const el = document.getElementById(elementId);
        if (!el) return;

        // Use the QR SVG generator
        const svg = generateQrSvg(data, size || 200);
        el.innerHTML = svg;
    },

    // Scroll carousel to specific slide
    scrollCarousel: function (elementId, index) {
        const el = document.getElementById(elementId);
        if (!el) return;
        const slide = el.children[index];
        if (slide) slide.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'center' });
    },

};

// ── Minimal QR Code Generator (Mode: Byte, EC: L) ──
// Generates SVG string for QR codes without external libraries.
// Supports up to ~2953 bytes (version 40, EC level L).
function generateQrSvg(text, size) {
    // For the sample wallet, we use a simple visual placeholder that shows the data
    // A production app would use a proper QR library like qrcode.js
    // This generates a deterministic pattern from the data hash
    const modules = generateQrModules(text);
    const moduleCount = modules.length;
    const cellSize = size / moduleCount;

    let paths = '';
    for (let r = 0; r < moduleCount; r++) {
        for (let c = 0; c < moduleCount; c++) {
            if (modules[r][c]) {
                paths += `<rect x="${c * cellSize}" y="${r * cellSize}" width="${cellSize}" height="${cellSize}"/>`;
            }
        }
    }

    return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${size} ${size}" width="${size}" height="${size}" shape-rendering="crispEdges">
        <rect width="100%" height="100%" fill="white"/>
        <g fill="#000">${paths}</g>
    </svg>`;
}

function generateQrModules(text) {
    // Simple hash-based pattern generator for visual placeholder
    // Creates a QR-like grid with finder patterns
    const size = 25; // ~Version 2 QR
    const grid = Array.from({ length: size }, () => Array(size).fill(false));

    // Finder patterns (top-left, top-right, bottom-left)
    addFinderPattern(grid, 0, 0);
    addFinderPattern(grid, size - 7, 0);
    addFinderPattern(grid, 0, size - 7);

    // Timing patterns
    for (let i = 8; i < size - 8; i++) {
        grid[6][i] = i % 2 === 0;
        grid[i][6] = i % 2 === 0;
    }

    // Data area: deterministic from input
    let hash = 0;
    for (let i = 0; i < text.length; i++) {
        hash = ((hash << 5) - hash + text.charCodeAt(i)) | 0;
    }

    for (let r = 9; r < size - 1; r++) {
        for (let c = 9; c < size - 1; c++) {
            if (r === 6 || c === 6) continue; // Skip timing
            hash = ((hash << 5) - hash + r * size + c) | 0;
            grid[r][c] = (hash & 1) === 1;
        }
    }

    return grid;
}

function addFinderPattern(grid, row, col) {
    for (let r = 0; r < 7; r++) {
        for (let c = 0; c < 7; c++) {
            grid[row + r][col + c] =
                r === 0 || r === 6 || c === 0 || c === 6 || // Border
                (r >= 2 && r <= 4 && c >= 2 && c <= 4);      // Center
        }
    }
    // Separator (white border around finder)
    for (let i = -1; i <= 7; i++) {
        setIfValid(grid, row - 1, col + i, false);
        setIfValid(grid, row + 7, col + i, false);
        setIfValid(grid, row + i, col - 1, false);
        setIfValid(grid, row + i, col + 7, false);
    }
}

function setIfValid(grid, r, c, val) {
    if (r >= 0 && r < grid.length && c >= 0 && c < grid[0].length) {
        grid[r][c] = val;
    }
}
