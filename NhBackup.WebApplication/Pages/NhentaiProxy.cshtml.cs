using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;

namespace NhBackup.WebApplication.Pages;

public class NhentaiProxyModel : PageModel
{
    public IActionResult OnGet([FromQuery] string urls)
    {
        List<string> baseUrls;
        try
        {
            baseUrls = JsonSerializer.Deserialize<List<string>>(urls) ?? new List<string>();
        }
        catch
        {
            baseUrls = new List<string>();
        }

        baseUrls = baseUrls
            .Where(u => !string.IsNullOrWhiteSpace(u) && u.StartsWith("http"))
            .ToList();

        if (baseUrls.Count == 0)
            return BadRequest("No valid URLs provided");

        return Content(GenerateScript(baseUrls), "application/javascript");
    }

    private static string GenerateScript(List<string> baseUrls)
    {
        var urlsJs = string.Join(",\n        ",
            baseUrls.Select(u => $"'{u}/downloads'"));

        var connectDirectives = string.Join("\n",
            baseUrls.Select(u => $"// @connect      {new Uri(u).Host}"));

        return $$"""
// ==UserScript==
// @name         nhentai Image Proxy
// @namespace    https://nhentai.net/
// @version      7.0
// @description  Serve nhentai images from selfhosted server instead of CDN
// @author       https://github.com/sharpsalat
// @match        https://nhentai.net/*
// @icon         https://www.google.com/s2/favicons?sz=64&domain=nhentai.net
// @grant        GM_xmlhttpRequest
{{connectDirectives}}
// ==/UserScript==

(function () {
    'use strict';

    // ── Configuration ────────────────────────────────────────────────────────

    const BASE_URLS = [
        {{urlsJs}}
    ];

    const EXTENSIONS = ['jpg', 'webp', 'png', 'gif'];

    // Timeout for HEAD requests when checking if a file exists on your server.
    // If your server doesn't respond within this time it is treated as missing.
    // For local/LAN servers 100ms is plenty. Increase for remote/slow hosts.
    const HEAD_TIMEOUT_MS = 100;

    // Timeout for background health checks (runs silently, does not block image loading).
    const HEALTH_TIMEOUT_MS = 3000;

    // How often to re-ping servers (ms).
    const HEALTH_INTERVAL_MS = 15_000;

    // Health endpoint on your server (ASP.NET Core: app.MapHealthChecks("/healthz")).
    const HEALTH_PATH = '/healthz';

    // ── Server health ────────────────────────────────────────────────────────
    // null  = first check still in-flight (treated as maybe-alive)
    // true  = reachable
    // false = down — skipped entirely until next health interval

    const serverHealth = Object.fromEntries(BASE_URLS.map(b => [b, null]));

    function pingServer(base) {
        const root = base.replace('/downloads', '');
        GM_xmlhttpRequest({
            method: 'HEAD',
            url: `${root}${HEALTH_PATH}`,
            timeout: HEALTH_TIMEOUT_MS,
            onload:    (r) => { serverHealth[base] = r.status === 200;
                                console.log(`[proxy] health ${root}: ${serverHealth[base] ? 'UP' : 'DOWN'}`); },
            onerror:   ()  => { serverHealth[base] = false;
                                console.log(`[proxy] health ${root}: DOWN (error)`); },
            ontimeout: ()  => { serverHealth[base] = false;
                                console.log(`[proxy] health ${root}: DOWN (timeout)`); },
        });
    }

    function activeBases() {
        return BASE_URLS.filter(b => serverHealth[b] !== false);
    }

    BASE_URLS.forEach(base => pingServer(base));
    setInterval(() => BASE_URLS.forEach(base => pingServer(base)), HEALTH_INTERVAL_MS);

    // ── File cache ───────────────────────────────────────────────────────────

    const fileCache = {};

    function findLocalFile(galleryId, paddedNum) {
        const bases = activeBases();
        if (bases.length === 0) return Promise.resolve(null);

        const candidates = bases.flatMap(base =>
            EXTENSIONS.map(ext => `${base}/${galleryId}/${paddedNum}.${ext}`)
        );

        // Serve from cache instantly if possible
        for (const url of candidates) {
            if (fileCache[url] === true) return Promise.resolve(url);
        }

        return new Promise((resolve) => {
            let resolved  = false;
            let completed = 0;
            const total   = candidates.length;

            function tick() {
                completed++;
                if (completed === total && !resolved) resolve(null);
            }

            candidates.forEach((url) => {
                if (fileCache[url] === false) { tick(); return; }

                GM_xmlhttpRequest({
                    method: 'HEAD',
                    url,
                    timeout: HEAD_TIMEOUT_MS,
                    onload: (r) => {
                        const ok = r.status >= 200 && r.status < 400;
                        fileCache[url] = ok;
                        if (ok && !resolved) { resolved = true; resolve(url); }
                        tick();
                    },
                    onerror:   () => { fileCache[url] = false; tick(); },
                    ontimeout: () => { fileCache[url] = false; tick(); },
                });
            });
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    function getGalleryId() {
        const m = location.pathname.match(/\/g\/(\d+)/);
        return m ? m[1] : null;
    }

    function getTotalPages() {
        const el = document.querySelector('.num-pages');
        if (el) {
            const n = parseInt(el.textContent.trim(), 10);
            if (!isNaN(n)) return n;
        }
        for (const s of document.querySelectorAll('script[type="application/json"][data-sveltekit-fetched]')) {
            try {
                const data = JSON.parse(s.textContent);
                const body = data.body
                    ? (typeof data.body === 'string' ? JSON.parse(data.body) : data.body)
                    : null;
                if (body?.num_pages) return body.num_pages;
            } catch {}
        }
        return null;
    }

    function padPage(num, total) {
        return String(num).padStart(total ? String(total).length : 1, '0');
    }

    function extractPageNum(src) {
        const m = src.match(/\/(\d+)\.(jpg|png|gif|webp)$/i);
        return m ? parseInt(m[1], 10) : null;
    }

    function isNhentaiCDN(src) {
        return /https?:\/\/i\d+\.nhentai\.net\/galleries\//i.test(src);
    }

    // ── src interceptor ──────────────────────────────────────────────────────

    const nativeDesc    = Object.getOwnPropertyDescriptor(HTMLImageElement.prototype, 'src');
    const nativeSetAttr = Element.prototype.setAttribute;
    const pendingMap    = new WeakMap();

    function intercept(img, cdnSrc) {
        const galleryId = getGalleryId();
        if (!galleryId)              { nativeDesc.set.call(img, cdnSrc); return; }
        if (activeBases().length === 0) { nativeDesc.set.call(img, cdnSrc); return; }

        const pageNum = extractPageNum(cdnSrc);
        if (pageNum === null)        { nativeDesc.set.call(img, cdnSrc); return; }

        const paddedNum = padPage(pageNum, getTotalPages());

        nativeDesc.set.call(img, '');
        pendingMap.set(img, cdnSrc);

        console.log(`[proxy] intercepted page ${paddedNum}`);

        findLocalFile(galleryId, paddedNum).then((foundUrl) => {
            if (pendingMap.get(img) !== cdnSrc) {
                console.log(`[proxy] page ${paddedNum}: src changed while checking, abort`);
                return;
            }
            pendingMap.delete(img);

            if (foundUrl) {
                console.log(`[proxy] page ${paddedNum} — LOCAL:        ${foundUrl}`);
                console.log(`[proxy] page ${paddedNum} — CDN (blocked): ${cdnSrc}`);
                nativeDesc.set.call(img, foundUrl);
            } else {
                console.log(`[proxy] page ${paddedNum} — not found locally, CDN: ${cdnSrc}`);
                nativeDesc.set.call(img, cdnSrc);
            }
        });
    }

    Object.defineProperty(HTMLImageElement.prototype, 'src', {
        get() { return nativeDesc.get.call(this); },
        set(value) {
            if (isNhentaiCDN(value)) intercept(this, value);
            else nativeDesc.set.call(this, value);
        },
        configurable: true,
    });

    Element.prototype.setAttribute = function (name, value) {
        if (name === 'src' && this instanceof HTMLImageElement && isNhentaiCDN(value)) {
            this.src = value;
        } else {
            nativeSetAttr.call(this, name, value);
        }
    };

    console.log(`[proxy] ready — ${BASE_URLS.length} server(s):`, BASE_URLS);
})();
""";
    }
}