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
            baseUrls = JsonSerializer.Deserialize<List<string>>(urls)
                       ?? new List<string>();
        }
        catch
        {
            baseUrls = new List<string>();
        }

        // Filter to valid URLs only
        baseUrls = baseUrls
            .Where(u => !string.IsNullOrWhiteSpace(u) && u.StartsWith("http"))
            .ToList();

        if (baseUrls.Count == 0)
            return BadRequest("No valid URLs provided");

        var script = GenerateScript(baseUrls);

        return Content(script, "application/javascript");
    }

    private static string GenerateScript(List<string> baseUrls)
    {
        // Serialize URL list into JS array literal
        var urlsJs = string.Join(",\n        ",
            baseUrls.Select(u => $"'{u}/downloads'"));

        return $$"""
// ==UserScript==
// @name         nhentai Local Image Proxy
// @namespace    https://nhentai.net/
// @version      6.0
// @description  Redirect nhentai images to local server if file exists
// @author       https://github.com/sharpsalat
// @match        https://nhentai.net/*
// @icon         https://www.google.com/s2/favicons?sz=64&domain=nhentai.net
// @grant        GM_xmlhttpRequest
{{string.Join("\n", baseUrls.Select(u => $"// @connect      {new Uri(u).Host}"))}}
// ==/UserScript==

(function () {
    'use strict';

    // Base URLs in priority order — first responding wins
    const BASE_URLS = [
        {{urlsJs}}
    ];

    const EXTENSIONS = ['jpg', 'webp', 'png', 'gif'];

    function getGalleryId() {
        const match = location.pathname.match(/\/g\/(\d+)/);
        return match ? match[1] : null;
    }

    function getTotalPages() {
        const el = document.querySelector('.num-pages');
        if (el) {
            const n = parseInt(el.textContent.trim(), 10);
            if (!isNaN(n)) return n;
        }
        const scripts = document.querySelectorAll('script[type="application/json"][data-sveltekit-fetched]');
        for (const script of scripts) {
            try {
                const data = JSON.parse(script.textContent);
                if (data.body) {
                    const body = typeof data.body === 'string' ? JSON.parse(data.body) : data.body;
                    if (body.num_pages) return body.num_pages;
                }
            } catch (e) {}
        }
        return null;
    }

    function padPage(num, total) {
        const digits = total ? String(total).length : 1;
        return String(num).padStart(digits, '0');
    }

    function extractPageNum(src) {
        const match = src.match(/\/(\d+)\.(jpg|png|gif|webp)$/i);
        return match ? { num: parseInt(match[1], 10) } : null;
    }

    function isNhentaiCDN(src) {
        return /https?:\/\/i\d+\.nhentai\.net\/galleries\//i.test(src);
    }

    const cache = {};

    // Try one specific URL via HEAD
    function headCheck(url) {
        if (url in cache) return Promise.resolve(cache[url]);
        return new Promise((resolve) => {
            GM_xmlhttpRequest({
                method: 'HEAD', url, timeout: 2000,
                onload:    (r) => { cache[url] = r.status >= 200 && r.status < 400; resolve(cache[url]); },
                onerror:   ()  => { cache[url] = false; resolve(false); },
                ontimeout: ()  => { cache[url] = false; resolve(false); },
            });
        });
    }

    // Check all base URLs * all extensions, return first found
    async function findLocalFile(galleryId, paddedNum) {
        for (const base of BASE_URLS) {
            for (const ext of EXTENSIONS) {
                const url = `${base}/${galleryId}/${paddedNum}.${ext}`;
                const ok = await headCheck(url);
                if (ok) return url;
            }
        }
        return null;
    }

    const nativeDescriptor = Object.getOwnPropertyDescriptor(HTMLImageElement.prototype, 'src');
    const pendingMap = new WeakMap();

    Object.defineProperty(HTMLImageElement.prototype, 'src', {
        get() { return nativeDescriptor.get.call(this); },
        set(value) {
            if (!isNhentaiCDN(value)) { nativeDescriptor.set.call(this, value); return; }

            const galleryId = getGalleryId();
            if (!galleryId) { nativeDescriptor.set.call(this, value); return; }

            // Block browser — set empty src immediately
            nativeDescriptor.set.call(this, '');
            pendingMap.set(this, value);

            const img = this;
            const originalSrc = value;
            const parsed = extractPageNum(originalSrc);

            if (!parsed) { nativeDescriptor.set.call(img, originalSrc); return; }

            const total = getTotalPages();
            const paddedNum = padPage(parsed.num, total);

            console.log(`[proxy] Intercepted page ${paddedNum}, checking ${BASE_URLS.length} base(s)...`);

            findLocalFile(galleryId, paddedNum).then((foundUrl) => {
                if (pendingMap.get(img) !== originalSrc) {
                    console.log(`[proxy] src changed while waiting, skipping`);
                    return;
                }
                pendingMap.delete(img);

                if (foundUrl) {
                    console.log(`[proxy] SUCCESS page ${paddedNum}`);
                    console.log(`[proxy]   CDN (blocked): ${originalSrc}`);
                    console.log(`[proxy]   LOCAL:         ${foundUrl}`);
                    nativeDescriptor.set.call(img, foundUrl);
                } else {
                    console.log(`[proxy] NOT FOUND page ${paddedNum} on any base URL`);
                    console.log(`[proxy]   fallback CDN: ${originalSrc}`);
                    nativeDescriptor.set.call(img, originalSrc);
                }
            });
        },
        configurable: true,
    });

    const nativeSetAttr = Element.prototype.setAttribute;
    Element.prototype.setAttribute = function (name, value) {
        if (name === 'src' && this instanceof HTMLImageElement && isNhentaiCDN(value)) {
            this.src = value;
            return;
        }
        nativeSetAttr.call(this, name, value);
    };

    console.log(`[proxy] Installed — ${BASE_URLS.length} base URL(s):`, BASE_URLS);

})();
""";
    }
}