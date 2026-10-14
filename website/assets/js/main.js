/* ============================================================================
   Maktaby — landing page behaviour
   No dependencies. Five small jobs:
     1. theme toggle (persisted, defaults to the OS preference)
     2. release lookup  -> Stable + Beta download buttons, version, size, count,
                           and the per-architecture installer list
     3. reveal-on-scroll
     4. mobile nav + screenshot lightbox
     5. runtime strings for the current page language

   There is one page per language (index.html, index.ar.html) rather than a client-side
   dictionary over one document: the Arabic copy is then real HTML that search engines and
   link previews can read, and the reader gets the whole page at once instead of a flash of
   English. This file is shared by both, so it asks the document which language it is in.
   ========================================================================= */
(function () {
  'use strict';

  var REPO = 'ezhassen/Maktaby';
  var API = 'https://api.github.com/repos/' + REPO + '/releases?per_page=30';
  var REPO_API = 'https://api.github.com/repos/' + REPO;
  var STORAGE_THEME = 'maktaby-theme';

  // Release assets are named by the release workflow, not by this file:
  //   Maktaby-<version>-<arch>-setup.exe        framework-dependent
  //   Maktaby-<version>-<arch>-setup-full.exe   self-contained
  // Keep this regex in step with TARGETS_JSON in .github/workflows/release.yml. The
  // version and the product name are deliberately NOT matched, so the pattern is the
  // only thing that has to agree with the workflow.
  var ASSET_RE = /-(x64|arm64)-setup(-full)?\.exe$/i;
  var ARCHES = ['x64', 'arm64'];
  var FLAVORS = ['lean', 'full'];

  var reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  var isRtl = document.documentElement.dir === 'rtl' ||
              (document.documentElement.getAttribute('dir') || '').toLowerCase() === 'rtl';

  /* Strings this file generates. Everything else is in the page's own markup, where a
     translator can reach it without touching JavaScript. Keep the two languages in the
     same order — a missing key falls back to English rather than showing nothing. */
  var I18N = {
    en: {
      switchToLight: 'Switch to light theme',
      switchToDark: 'Switch to dark theme',
      openMenu: 'Open menu',
      closeMenu: 'Close menu',
      closeViewer: 'Close viewer',
      prevShot: 'Previous screenshot',
      nextShot: 'Next screenshot',
      viewerLabel: 'Screenshot viewer',
      noStableYet: 'No stable release yet',
      noStableShort: 'No stable release yet — beta is the only download',
      noStableBetaOnly: 'No stable release yet — beta only',
      latest: 'Latest:',
      beta: 'Beta:',
      forYou: 'for your PC',
      noBuildForArch: 'No installer published for this machine',
      leanNeeds: 'needs .NET 10 Desktop Runtime',
      fullBundled: 'self-contained — nothing to install first',
      downloads: 'downloads on GitHub Releases',
      stars: 'GitHub stars',
      chooseBuild: 'Choose a build',
      arm64Label: 'Windows on ARM'
    },
    ar: {
      switchToLight: 'التبديل إلى المظهر الفاتح',
      switchToDark: 'التبديل إلى المظهر الداكن',
      openMenu: 'فتح القائمة',
      closeMenu: 'إغلاق القائمة',
      closeViewer: 'إغلاق العارض',
      prevShot: 'الصورة السابقة',
      nextShot: 'الصورة التالية',
      viewerLabel: 'عارض لقطات الشاشة',
      noStableYet: 'لا يوجد إصدار مستقر بعد',
      noStableShort: 'لا يوجد إصدار مستقر بعد — الإصدار التجريبي هو التنزيل الوحيد',
      noStableBetaOnly: 'لا يوجد إصدار مستقر بعد — تجريبي فقط',
      latest: 'الأحدث:',
      beta: 'تجريبي:',
      forYou: 'لجهازك',
      noBuildForArch: 'لا يوجد مُثبِّت منشور لهذا الجهاز',
      leanNeeds: 'يتطلب .NET 10 Desktop Runtime',
      fullBundled: 'مستقل — لا يحتاج أي تثبيت مسبق',
      downloads: 'عملية تنزيل على إصدارات GitHub',
      stars: 'نجوم GitHub',
      chooseBuild: 'اختر النسخة المناسبة',
      arm64Label: 'ويندوز على معالج ARM'
    }
  };

  function t(key) {
    var lang = document.documentElement.lang || 'en';
    var table = I18N[lang] || I18N.en;
    return table[key] || I18N.en[key] || key;
  }

  function $(sel, root) { return (root || document).querySelector(sel); }
  function $$(sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); }

  /* ------------------------------- 1. theme -------------------------------- */
  function applyTheme(theme) {
    document.documentElement.setAttribute('data-theme', theme);
    var toggle = $('#theme-toggle');
    if (toggle) {
      toggle.setAttribute('aria-label', theme === 'dark' ? t('switchToLight') : t('switchToDark'));
      toggle.setAttribute('title', theme === 'dark' ? t('switchToLight') : t('switchToDark'));
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
    // The page's own language, not a hardcoded one: an Arabic reader gets Arabic
    // digit grouping. Latin digits are kept (see formatBytes) so the numbers stay
    // comparable with what the app and the release page show.
    return n.toLocaleString(document.documentElement.lang || 'en-US');
  }

  /* Which installer suits this machine.
     Chromium (Edge and Chrome, i.e. nearly every Windows visitor) exposes this through
     User-Agent Client Hints. On Windows-on-ARM, Chrome reports architecture "x86" for a
     64-bit tab, so the architecture token alone is not enough — the bitness is what
     separates "x64 on ARM under emulation" (which the x64 installer handles, and which
     is what most ARM64 machines will actually want) from a 32-bit tab (which we ship
     nothing for). Browsers without UA-CH return null rather than a guess, and the page
     then offers all four builds instead of silently picking one.
     Resolves to 'x64' | 'arm64' | 'x86' | null. */
  function detectArchitecture() {
    var hints = navigator.userAgentData;
    if (!hints || !hints.getHighEntropyValues) return Promise.resolve(null);

    return hints.getHighEntropyValues(['architecture', 'bitness'])
      .then(function (values) {
        if (values.architecture === 'arm') return 'arm64';
        if (values.architecture === 'x86' && values.bitness === '64') return 'x64';
        // 32-bit: no native build exists. Returning null (rather than 'x86') is what
        // makes the page offer every build instead of claiming one is "for your PC".
        return null;
      })
      .catch(function () { return null; });
  }

  /* The {arch, flavor, asset} triples a release actually publishes, in a stable order.
     Anything the workflow does not name is ignored on purpose: a fallback to "the first
     .exe in the list" is how a page ends up offering an ARM64 build to an x64 machine. */
  function findInstallers(release) {
    var assets = release.assets || [];
    var found = {};
    assets.forEach(function (asset) {
      var match = ASSET_RE.exec(asset.name || '');
      if (!match) return;
      var key = match[1].toLowerCase() + '/' + (match[2] ? 'full' : 'lean');
      // First wins: a release never publishes the same arch+flavor twice, and if it
      // ever did, the older one is the one already in the wild.
      if (!found[key]) found[key] = { arch: match[1].toLowerCase(), flavor: match[2] ? 'full' : 'lean', asset: asset };
    });

    var list = [];
    ARCHES.forEach(function (arch) {
      FLAVORS.forEach(function (flavor) {
        if (found[arch + '/' + flavor]) list.push(found[arch + '/' + flavor]);
      });
    });
    return list;
  }

  /* Picks the installer for a machine, or null when there is none.
     Lean first: it is a fraction of the size, and the page says what it needs. The full
     build stays one click away rather than being the default, because most people asking
     what to click want the small file, not the 60 MB one.

     The three cases are deliberately different:
       - detected arch, build published  -> that build
       - detected arch, nothing published -> null. Handing over the other architecture's
         installer would fail half-way through the install, so "we have nothing for this
         machine" is the honest answer and the page says so in words.
       - arch unknown (browser without UA-CH) -> the first listed build, because offering
         something beats offering nothing when there is no basis to choose. */
  function pickInstaller(list, arch) {
    if (!list.length) return null;
    if (!arch) return list[0];

    var forArch = list.filter(function (i) { return i.arch === arch; });
    if (!forArch.length) return null;

    return forArch.filter(function (i) { return i.flavor === 'lean'; })[0] || forArch[0];
  }

  // Fills the per-build table in the install section: every installer on the release,
  // with its real size, so a reader on the wrong architecture is never stuck guessing.
  function renderInstallers(list, arch) {
    var rows = $$('[data-installer-row]');
    if (!rows.length) return;

    var best = pickInstaller(list, arch);
    rows.forEach(function (row) {
      var wantArch = row.getAttribute('data-installer-arch');
      var wantFlavor = row.getAttribute('data-installer-flavor');
      var match = list.filter(function (i) { return i.arch === wantArch && i.flavor === wantFlavor; })[0];
      var link = $('a', row);
      var size = $('[data-installer-size]', row);

      if (!match) {
        link.removeAttribute('href');
        link.classList.add('is-disabled');
        link.setAttribute('aria-disabled', 'true');
        if (size) size.textContent = '—';
        return;
      }

      link.href = match.asset.browser_download_url;
      link.classList.remove('is-disabled');
      link.removeAttribute('aria-disabled');
      if (size) size.textContent = formatBytes(match.asset.size) || '—';

      // Mark the one the hero buttons point at, so the page never offers two equally
      // plausible "download this" answers.
      row.classList.toggle('is-recommended', !!(best && best.asset === match.asset));
    });

    var note = $('#install-detected');
    if (note) {
      if (!list.length || (arch && !pickInstaller(list, arch))) {
        note.hidden = false;
        note.textContent = t('noBuildForArch');
      } else {
        note.hidden = false;
        note.textContent = arch
          ? t('chooseBuild') + ' — ' + arch + ' ' + t('forYou')
          : t('chooseBuild');
      }
    }
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
        label.textContent = t('noStableYet');
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
    var label = $('[data-field="count-label"]', row);
    if (label) label.textContent = t('downloads');
    row.hidden = false;
  }

  function setStarsLabel(count) {
    var total = $('#stars-total');
    if (!total) return;
    var label = $('[data-field="stars-label"]', total);
    if (label) label.textContent = t('stars');
  }

  function renderReleases(releases, arch) {
    var stable = null;
    var beta = null;
    var totalDownloads = 0;

    releases.forEach(function (r) {
      if (r.draft) return;
      totalDownloads += r.download_count || 0;
      if (r.prerelease && !beta) beta = r;
      if (!r.prerelease && !stable) stable = r;
    });

    var latest = stable || beta;

    if (stable) {
      var list = findInstallers(stable);
      var sAsset = pickInstaller(list, arch);
      setStable(
        stable.tag_name,
        sAsset ? formatBytes(sAsset.asset.size) : null,
        sAsset ? sAsset.asset.browser_download_url : 'https://github.com/' + REPO + '/releases/latest'
      );
      renderInstallers(list, arch);
    } else {
      // No stable release: still show whatever the beta published, so a beta-only
      // project still gets a working download list.
      disableStable(t('noStableBetaOnly'));
      if (beta) renderInstallers(findInstallers(beta), arch);
    }

    var bList = beta && findInstallers(beta);
    var bAsset = bList && pickInstaller(bList, arch);
    setBeta(
      beta ? beta.tag_name : null,
      bAsset ? formatBytes(bAsset.asset.size) : null,
      bAsset ? bAsset.asset.browser_download_url : null
    );

    setDownloadCount(totalDownloads);

    if (!latest) renderInstallers([], arch);
  }

  function initReleases() {
    if (!window.fetch) return; // ancient browser: leave the releases-page links in place

    // Architecture first, then the release list: the buttons point at the build for
    // THIS machine, so the answer has to be in hand before anything is rendered.
    // detectArchitecture() never rejects, so a browser without UA-CH still renders.
    return detectArchitecture().then(function (arch) {
      return fetch(API, { headers: { Accept: 'application/vnd.github+json' } })
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
            if (Array.isArray(data) && data.length) renderReleases(data, arch);
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
    });
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
    setStarsLabel(count);
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
      toggle.setAttribute('aria-label', open ? t('closeMenu') : t('openMenu'));
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

  // Localise the lightbox chrome that lives in the markup, so an Arabic page reads
    // "الصورة التالية" instead of "Next screenshot".
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

    if (box) box.setAttribute('aria-label', t('viewerLabel'));
    if (closeBtn) closeBtn.setAttribute('aria-label', t('closeViewer'));
    if (prevBtn) prevBtn.setAttribute('aria-label', t('prevShot'));
    if (nextBtn) nextBtn.setAttribute('aria-label', t('nextShot'));

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
      // Right-to-left reading order: the arrow that moves forward is the one that
      // points the way the language reads. Same rule for the swipe below, so a
      // finger dragged the way the text goes moves forward too.
      if (e.key === 'ArrowLeft')  { show(current + (isRtl ? 1 : -1)); return; }
      if (e.key === 'ArrowRight') { show(current + (isRtl ? -1 : 1)); }
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
      // Swiping left-to-right moves forward in RTL, and right-to-left moves forward
      // in LTR — i.e. the gesture follows the reading direction, like the arrow keys.
      show(current + ((dx < 0) !== isRtl ? 1 : -1));
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