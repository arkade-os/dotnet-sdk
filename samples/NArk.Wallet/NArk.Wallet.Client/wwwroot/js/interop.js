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

    // Scroll carousel to specific slide
    scrollCarousel: function (elementId, index) {
        const el = document.getElementById(elementId);
        if (!el) return;
        const slide = el.children[index];
        if (slide) slide.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'center' });
    },

};
