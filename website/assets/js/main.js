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
  var REPO_API = 'https://api.github.com/repos/' + REPO;
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

  // The JSON-LD block ships without softwareVersion so a static copy of the page
  // can never advertise a version that is already outdated. Whichever release
  // the lookup picked gets stamped in here instead.
  function setSchemaVersion(tag) {
    if (!tag) return;
    var el = $('script[type="application/ld+json"]');
    if (!el) return;
    try {
      var data = JSON.parse(el.textContent);
      data.softwareVersion = String(tag).replace(/^v/, '');
      el.textContent = JSON.stringify(data, null, 2);
    } catch (e) { /* malformed block: structured data is a nicety, not a feature */ }
  }

  function setStable(version, size, href) {
    setSchemaVersion(version);

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

  /* ------------------------------ stars ----------------------------------- */
  function setStars(count) {
    // A zero-star badge is worse than no badge: it is social proof pointing the
    // wrong way. It appears on its own the moment the first star lands.
    if (typeof count !== 'number' || !isFinite(count) || count < 1) return;
    var text = count >= 1000 ? (count / 1000).toFixed(count >= 10000 ? 0 : 1).replace(/\.0$/, '') + 'k' : String(count);

    var badge = $('#stars-badge');
    if (badge) {
      badge.querySelector('[data-field="stars"]').textContent = text;
      badge.hidden = false;
    }

    var total = $('#stars-total');
    if (total) {
      total.querySelector('[data-field="stars"]').textContent = formatCount(count);
      total.hidden = false;
    }
  }

  // Deliberately a separate request from the release lookup: the two answers are
  // independent, so a failure or a rate-limit on either must not take the other
  // down with it. Two calls still fit comfortably in the 60/hour unauthenticated
  // budget for a page most people load once.
  function initStars() {
    if (!window.fetch) return;

    fetch(REPO_API, { headers: { Accept: 'application/vnd.github+json' } })
      .then(function (res) {
        if (!res.ok) throw new Error('GitHub API responded ' + res.status);
        return res.json();
      })
      .then(
        function (repo) { setStars(repo.stargazers_count); },
        function () { /* no star count is better than a wrong one */ }
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
    var countEl = $('#lightbox-count');
    var closeBtn = $('#lightbox-close');
    var prevBtn = $('#lightbox-prev');
    var nextBtn = $('#lightbox-next');
    var triggers = $$('[data-lightbox]');
    if (!box || !img || !triggers.length) return;

    var current = 0;
    var lastFocus = null;

    // The hero frame and the gallery's lead shot show the SAME capture, so the
    // raw trigger list contains a duplicate and the viewer would step through
    // the same image twice. Collapse the sequence to unique sources (first
    // occurrence wins, so the hero stays first) and resolve any duplicate
    // trigger to its canonical entry — clicking the gallery's dark shot opens
    // the same slot the hero does, rather than a second look at the same file.
    var seen = Object.create(null);
    var items = [];
    triggers.forEach(function (t) {
      var src = t.getAttribute('data-lightbox');
      if (seen[src]) return;
      seen[src] = true;
      items.push({ src: src, trigger: t });
    });

    // Prefer an alt/caption from the copy inside a <figure>: the hero trigger has
    // none, and the gallery entry for the same file does.
    var describe = function (src) {
      for (var i = 0; i < triggers.length; i++) {
        if (triggers[i].getAttribute('data-lightbox') !== src) continue;
        var figure = triggers[i].closest('figure');
        var figureImg = figure && figure.querySelector('img');
        if (figureImg) {
          return { alt: figureImg.alt || '', caption: triggers[i].getAttribute('data-caption') || '' };
        }
      }
      return { alt: '', caption: '' };
    };

    var indexOf = function (trigger) {
      var src = trigger.getAttribute('data-lightbox');
      for (var i = 0; i < items.length; i++) if (items[i].src === src) return i;
      return 0;
    };

    // Decode the neighbours so flipping through the loop never shows a blank
    // frame: the images are all local, so this is a handful of cached WebPs.
    function warm(from) {
      [1, 2, -1].forEach(function (step) {
        var preloader = new Image();
        preloader.src = items[(from + step + items.length) % items.length].src;
      });
    }

    var show = function (index) {
      current = (index + items.length) % items.length; // loops at both ends
      var item = items[current];
      var text = describe(item.src);

      img.src = item.src;
      img.alt = text.alt;
      caption.textContent = text.caption;
      if (countEl) countEl.textContent = current + 1 + ' / ' + items.length;

      warm(current);
    };

    var open = function (trigger) {
      show(indexOf(trigger));
      lastFocus = trigger;
      box.hidden = false;
      document.body.classList.add('no-scroll');
      if (prevBtn && nextBtn) {
        var single = items.length < 2;
        prevBtn.hidden = single;
        nextBtn.hidden = single;
      }
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

    if (prevBtn) prevBtn.addEventListener('click', function () { show(current - 1); });
    if (nextBtn) nextBtn.addEventListener('click', function () { show(current + 1); });
    closeBtn.addEventListener('click', close);
    box.addEventListener('click', function (e) { if (e.target === box) close(); });

    document.addEventListener('keydown', function (e) {
      if (box.hidden) return;
      if (e.key === 'Escape') { close(); return; }
      if (e.key === 'ArrowLeft') { show(current - 1); return; }
      if (e.key === 'ArrowRight') { show(current + 1); }
    });

    // Swipe: the primary way to move through a gallery on a phone, and the
    // arrows are not reachable with a thumb on a narrow screen.
    var touchX = null;
    box.addEventListener('touchstart', function (e) {
      touchX = e.changedTouches[0].clientX;
    }, { passive: true });
    box.addEventListener('touchend', function (e) {
      if (touchX === null) return;
      var dx = e.changedTouches[0].clientX - touchX;
      touchX = null;
      if (Math.abs(dx) < 45) return;
      show(current + (dx < 0 ? 1 : -1));
    }, { passive: true });
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
    initStars();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();