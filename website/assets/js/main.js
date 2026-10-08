/* ============================================================================
   Maktaby — landing page behaviour
   No dependencies. Four small jobs:
     1. theme toggle (persisted, defaults to the OS preference)
     2. release lookup  -> Stable + Beta download buttons, version, size, count
     3. reveal-on-scroll
     4. mobile nav + screenshot lightbox
   ========================================================================= */
(function () {
  'use strict';

  var REPO = 'ezhassen/Maktaby';
  var API = 'https://api.github.com/repos/' + REPO + '/releases?per_page=30';
  var STORAGE_THEME = 'maktaby-theme';

  var reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  function $(sel, root) { return (root || document).querySelector(sel); }
  function $$(sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); }

  /* ------------------------------- 1. theme -------------------------------- */
  function applyTheme(theme) {
    document.documentElement.setAttribute('data-theme', theme);
    var toggle = $('#theme-toggle');
    if (toggle) {
      toggle.setAttribute('aria-label', theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme');
    }
  }

  function initTheme() {
    var stored = null;
    try { stored = localStorage.getItem(STORAGE_THEME); } catch (e) { /* private mode */ }
    applyTheme(stored === 'dark' || stored === 'light' ? stored : document.documentElement.getAttribute('data-theme') || 'dark');

    var toggle = $('#theme-toggle');
    if (!toggle) return;
    toggle.addEventListener('click', function () {
      var next = document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
      applyTheme(next);
      try { localStorage.setItem(STORAGE_THEME, next); } catch (e) { /* ignore */ }
    });
  }

  /* ----------------------------- 2. releases ------------------------------ */
  function formatBytes(bytes) {
    if (!bytes && bytes !== 0) return null;
    var mb = bytes / (1024 * 1024);
    if (mb >= 100) return Math.round(mb) + ' MB';
    return (Math.round(mb * 10) / 10).toString().replace(/\.0$/, '') + ' MB';
  }

  function formatCount(n) {
    return n.toLocaleString('en-US');
  }

  // The installer is the only .exe this project publishes, and the release
  // workflow already renames it to the conventional "-x64-setup" suffix.
  function findInstaller(release) {
    var assets = release.assets || [];
    for (var i = 0; i < assets.length; i++) {
      if (/-x64-setup\.exe$/i.test(assets[i].name)) return assets[i];
    }
    for (var j = 0; j < assets.length; j++) {
      if (/\.exe$/i.test(assets[j].name)) return assets[j];
    }
return null;
  }

  function setStable(version, size, href) {
    var versionRow = $('#stable-version');
    if (versionRow) {
      versionRow.querySelector('[data-field="tag"]').textContent = version;
      versionRow.hidden = false;
    }

    var sizeRow = $('#stable-size');
    if (sizeRow && size) {
      sizeRow.querySelector('[data-field="size"]').textContent = size;
      sizeRow.hidden = false;
    }

    $$('#download-stable, #download-stable-2').forEach(function (a) {
      a.href = href;
      a.classList.remove('is-disabled');
      a.removeAttribute('aria-disabled');
      var label = a.querySelector('.btn-label');
      // Undo disableStable(): the CTA button carries no icon, so only the text
      // node ever changes and the original caption always stays recoverable.
      if (label && label.dataset.original) label.textContent = label.dataset.original;
    });
  }

  function disableStable(reason) {
    // One message, one place. #stable-version carries "Latest: <tag>" and would
    // otherwise repeat #stable-note verbatim on the same line.
    var versionRow = $('#stable-version');
    if (versionRow) versionRow.hidden = true;

    var note = $('#stable-note');
    if (note) {
      note.querySelector('[data-field="note"]').textContent = reason;
      note.hidden = false;
    }

    $$('#download-stable, #download-stable-2').forEach(function (a) {
      a.classList.add('is-disabled');
      a.setAttribute('aria-disabled', 'true');
      a.removeAttribute('href');
      var label = a.querySelector('.btn-label');
      if (label) {
        if (!label.dataset.original) label.dataset.original = label.textContent;
        label.textContent = 'No stable release yet';
      }
    });
  }

  function setBeta(version, size, href) {
    var row = $('#beta-version');
    if (!row) return;
    if (!version) { row.hidden = true; return; }
    row.querySelector('[data-field="tag"]').textContent = version + (size ? ' · ' + size : '');
    row.hidden = false;
    if (href) $('#download-beta').href = href;
  }

  function setDownloadCount(total) {
    var row = $('#downloads-total');
    if (!row || !total) return;
    row.querySelector('[data-field="count"]').textContent = formatCount(total);
    row.hidden = false;
  }

  function renderReleases(releases) {
    var stable = null;
    var beta = null;
    var totalDownloads = 0;

    releases.forEach(function (r) {
      if (r.draft) return;
      totalDownloads += r.download_count || 0;
      if (r.prerelease && !beta) beta = r;
      if (!r.prerelease && !stable) stable = r;
    });

    if (stable) {
      var sAsset = findInstaller(stable);
      setStable(
        stable.tag_name,
        sAsset ? formatBytes(sAsset.size) : null,
        sAsset ? sAsset.browser_download_url : 'https://github.com/' + REPO + '/releases/latest'
      );
    } else {
      disableStable('No stable release yet — beta only');
    }

    var bAsset = beta && findInstaller(beta);
    setBeta(
      beta ? beta.tag_name : null,
      bAsset ? formatBytes(bAsset.size) : null,
      bAsset ? bAsset.browser_download_url : null
    );

    setDownloadCount(totalDownloads);
  }

  function initReleases() {
    if (!window.fetch) return; // ancient browser: leave the releases-page links in place

    fetch(API, { headers: { Accept: 'application/vnd.github+json' } })
      .then(function (res) {
        // 403/429 = rate limited, 404 = no releases at all. Either way there is
        // nothing to render, so fall through to the graceful no-op path.
        if (!res.ok) throw new Error('GitHub API responded ' + res.status);
        return res.json();
      })
      // Two-argument then() on purpose: the rejection handler only ever sees
      // fetch/json failures, never a throw from renderReleases(). A typo in the
      // render path must surface as a console error, not be swallowed here as if
      // the network had simply been unavailable.
      .then(
        function (data) {
          if (Array.isArray(data) && data.length) renderReleases(data);
        },
        function (err) {
          // Unauthenticated requests are rate-limited to 60/hour per IP, and an
          // offline visitor gets the same failure. The buttons already point at
          // the releases page, which is the correct fallback either way.
          if (window.console && console.debug) {
            console.debug('[maktaby] release lookup skipped:', err.message);
          }
        }
      );
  }

  /* ------------------------------- 3. reveals ------------------------------ */
  function initReveals() {
    var items = $$('.reveal');
    items.forEach(function (el) {
      var delay = el.getAttribute('data-reveal-delay');
      if (delay) el.style.setProperty('--reveal-delay', delay);
    });

    if (reducedMotion || !('IntersectionObserver' in window)) {
      items.forEach(function (el) { el.classList.add('is-in'); });
      return;
    }

    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        entry.target.classList.add('is-in');
        io.unobserve(entry.target); // one-shot: no permanent observer cost
      });
    }, { rootMargin: '0px 0px -8% 0px', threshold: 0.06 });

    items.forEach(function (el) { io.observe(el); });
  }

  /* ------------------------------ 4. chrome -------------------------------- */
  function initHeader() {
    var header = $('#site-header');
    if (!header) return;
    var onScroll = function () {
      header.classList.toggle('is-stuck', window.scrollY > 8);
    };
    onScroll();
    window.addEventListener('scroll', onScroll, { passive: true });
  }

  function initNav() {
    var toggle = $('#nav-toggle');
    var nav = $('#site-nav');
    if (!toggle || !nav) return;

    var setOpen = function (open) {
      nav.classList.toggle('is-open', open);
      toggle.setAttribute('aria-expanded', String(open));
      toggle.setAttribute('aria-label', open ? 'Close menu' : 'Open menu');
    };

    toggle.addEventListener('click', function () {
      setOpen(!nav.classList.contains('is-open'));
    });
    $$('a', nav).forEach(function (a) {
      a.addEventListener('click', function () { setOpen(false); });
    });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && nav.classList.contains('is-open')) {
        setOpen(false);
        toggle.focus();
      }
    });
  }

  function initLightbox() {
    var box = $('#lightbox');
    var img = $('#lightbox-img');
    var caption = $('#lightbox-caption');
    var closeBtn = $('#lightbox-close');
    var triggers = $$('[data-lightbox]');
    if (!box || !img || !triggers.length) return;

    var lastFocus = null;

    var open = function (trigger) {
      img.src = trigger.getAttribute('data-lightbox');
      img.alt = (trigger.closest('figure') && trigger.closest('figure').querySelector('img').alt) || '';
      caption.textContent = trigger.getAttribute('data-caption') || '';
      lastFocus = trigger;
      box.hidden = false;
      document.body.classList.add('no-scroll');
      closeBtn.focus();
    };

    var close = function () {
      box.hidden = true;
      document.body.classList.remove('no-scroll');
      if (lastFocus) lastFocus.focus();
    };

    triggers.forEach(function (t) {
      t.addEventListener('click', function () { open(t); });
    });

    closeBtn.addEventListener('click', close);
    box.addEventListener('click', function (e) { if (e.target === box) close(); });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && !box.hidden) close();
    });
  }

  /* ------------------------------- 5. year --------------------------------- */
  function initYear() {
    $$('[data-year]').forEach(function (el) {
      el.textContent = String(new Date().getFullYear());
    });
  }

  /* -------------------------------- boot ----------------------------------- */
  function boot() {
    initTheme();
    initHeader();
    initNav();
    initReveals();
    initLightbox();
    initYear();
    initReleases();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();