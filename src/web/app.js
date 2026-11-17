(() => {
  'use strict';
  const host = window.chrome && window.chrome.webview;
  const send = (m) => { if (host) host.postMessage(JSON.stringify(m)); };
  const $ = (id) => document.getElementById(id);
  const root = document.documentElement;
  const view = $('view'), empty = $('empty'), tocList = $('tocList'), tabsBar = $('tabs');

  /** @type {Map<number, {id:number, host:string, name:string, path:string, el:HTMLElement, tabEl:HTMLElement, scroll:number, canBack:boolean}>} */
  const tabs = new Map();
  let active = null;
  let headings = [];

  const store = {
    get(k) { try { return localStorage.getItem(k); } catch { return null; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* sin almacenamiento */ } }
  };

  marked.setOptions({ gfm: true, breaks: false });

  /* ---------------- tema ---------------- */
  const THEMES = ['auto', 'light', 'dark'];
  const THEME_NAME = { auto: 'automático (sistema)', light: 'claro', dark: 'oscuro' };
  const THEME_ICON = {
    auto: '<circle cx="12" cy="12" r="8"/><path d="M12 4a8 8 0 0 1 0 16z" class="fill"/>',
    light: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
    dark: '<path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z"/>'
  };
  const mq = matchMedia('(prefers-color-scheme: dark)');
  let theme = THEMES.includes(store.get('theme')) ? store.get('theme') : 'auto';

  function applyTheme() {
    root.dataset.theme = theme;
    const dark = theme === 'dark' || (theme === 'auto' && mq.matches);
    $('hlLight').disabled = dark;
    $('hlDark').disabled = !dark;
    $('icoTheme').innerHTML = THEME_ICON[theme];
    $('btnTheme').title = 'Tema: ' + THEME_NAME[theme];
    send({ type: 'theme', dark });
  }
  mq.addEventListener('change', applyTheme);
  $('btnTheme').addEventListener('click', () => {
    theme = THEMES[(THEMES.indexOf(theme) + 1) % THEMES.length];
    store.set('theme', theme);
    applyTheme();
    toast('Tema ' + THEME_NAME[theme]);
  });

  /* ---------------- índice ---------------- */
  const narrow = () => innerWidth < 800;
  const sideOpen = () => document.body.classList.contains('toc-open');
  function setToc(open, remember = true) {
    document.body.classList.toggle('toc-open', open);
    $('btnToc').setAttribute('aria-pressed', String(open));
    if (remember) store.set('toc', open ? '1' : '0');
    if (open) { syncTree(); markActive(); }
  }
  const toggleToc = () => setToc(!sideOpen());
  $('btnToc').addEventListener('click', toggleToc);

  /* ---------------- pestañas del panel: índice / archivos ---------------- */
  let pane = store.get('pane') === 'files' ? 'files' : 'index';
  function setPane(p, remember = true) {
    pane = p;
    $('paneIndex').hidden = p !== 'index';
    $('paneFiles').hidden = p !== 'files';
    $('sideTabIndex').setAttribute('aria-selected', String(p === 'index'));
    $('sideTabFiles').setAttribute('aria-selected', String(p === 'files'));
    if (remember) store.set('pane', p);
    if (p === 'index') markActive();
    syncTree();
  }
  document.querySelectorAll('.side-tabs button').forEach((b) => {
    b.addEventListener('click', () => setPane(b.dataset.pane));
  });

  /* ---------------- árbol de carpetas ---------------- */
  const ICON_DIR = '<svg class="chev" viewBox="0 0 12 12"><path d="M4.5 2.5L8 6l-3.5 3.5"/></svg>' +
    '<svg class="ico" viewBox="0 0 24 24"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>';
  const ICON_FILE = '<svg class="ico" viewBox="0 0 24 24"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5M9 13h6M9 17h4"/></svg>';

  const tree = (() => {
    const ul = $('tree');
    const waiting = new Map();
    let root = null, parent = null, reqN = 0, busy = Promise.resolve();
    const norm = (p) => String(p || '').toLowerCase().replace(/\\+$/, '');
    const inside = (p, dir) => { const a = norm(p), b = norm(dir); return a === b || a.startsWith(b + '\\'); };

    function list(path) {
      return new Promise((resolve) => {
        const req = String(++reqN);
        waiting.set(req, resolve);
        send({ type: 'listDir', path, req });
      });
    }
    function onDir(m) {
      const r = waiting.get(m.req);
      if (r) { waiting.delete(m.req); r(m); }
    }

    function note(container, text) {
      const li = document.createElement('li');
      li.className = 'note';
      li.textContent = text;
      container.appendChild(li);
    }

    function fill(container, d) {
      container.innerHTML = '';
      if (d.error) { note(container, 'Sin acceso a esta carpeta'); return; }
      if (!d.dirs.length && !d.files.length) { note(container, 'Sin documentos Markdown'); return; }
      for (const dir of d.dirs) {
        const li = document.createElement('li');
        li.className = 'dir';
        li.dataset.path = dir.path;
        li.setAttribute('role', 'treeitem');
        const row = document.createElement('div');
        row.className = 'row';
        row.title = dir.path;
        row.innerHTML = ICON_DIR + '<span class="label"></span>';
        row.querySelector('.label').textContent = dir.name;
        row.addEventListener('click', () => toggle(li));
        row.addEventListener('dblclick', (e) => { e.preventDefault(); go(dir.path); });
        const sub = document.createElement('ul');
        sub.hidden = true;
        li.append(row, sub);
        container.appendChild(li);
      }
      for (const f of d.files) {
        const li = document.createElement('li');
        li.className = 'file';
        li.dataset.path = f.path;
        li.setAttribute('role', 'treeitem');
        const row = document.createElement('div');
        row.className = 'row';
        row.title = f.path;
        row.innerHTML = ICON_FILE + '<span class="label"></span>';
        row.querySelector('.label').textContent = f.name;
        row.addEventListener('click', () => send({ type: 'openFile', path: f.path }));
        li.appendChild(row);
        container.appendChild(li);
      }
    }

    async function toggle(li, open) {
      const want = open === undefined ? !li.classList.contains('expanded') : open;
      const sub = li.querySelector(':scope > ul');
      if (want && !li.dataset.loaded) {
        fill(sub, await list(li.dataset.path));
        li.dataset.loaded = '1';
      }
      li.classList.toggle('expanded', want);
      sub.hidden = !want;
      if (want) markOpen();
    }

    async function setRoot(path) {
      let d = await list(path);
      if (d.error && path) d = await list('');
      root = d.path;
      parent = d.parent;
      store.set('treeRoot', root);
      $('treeRoot').textContent = d.name;
      $('treeRoot').title = d.path;
      $('treeUp').disabled = !parent;
      fill(ul, d);
      markOpen();
    }

    // Abre la cadena de carpetas desde la raíz hasta la del documento
    async function expandTo(dir) {
      const segs = dir.replace(/\\+$/, '').slice(root.replace(/\\+$/, '').length).split('\\').filter(Boolean);
      let container = ul, cur = norm(root);
      for (const seg of segs) {
        cur += '\\' + seg.toLowerCase();
        const li = [...container.children].find((x) => x.classList.contains('dir') && norm(x.dataset.path) === cur);
        if (!li) break;
        await toggle(li, true);
        container = li.querySelector(':scope > ul');
      }
    }

    function reveal(docPath, docDir, force) {
      busy = busy.then(async () => {
        if (!docDir) return;
        if (force || !root || !inside(docDir, root)) await setRoot(docDir);
        await expandTo(docDir);
        markOpen();
        const cur = ul.querySelector('.row.current');
        if (cur) cur.scrollIntoView({ block: 'nearest' });
      }).catch(() => {});
      return busy;
    }

    function go(path) {
      busy = busy.then(() => setRoot(path)).catch(() => {});
      return busy;
    }

    function refresh() {
      busy = busy.then(async () => {
        const open = [...ul.querySelectorAll('li.dir.expanded')].map((li) => li.dataset.path)
          .sort((a, b) => a.length - b.length);
        await setRoot(root || '');
        for (const p of open) {
          const li = [...ul.querySelectorAll('li.dir')].find((x) => norm(x.dataset.path) === norm(p));
          if (li) await toggle(li, true);
        }
        markOpen();
      }).catch(() => {});
      return busy;
    }

    function markOpen() {
      const t = tabs.get(active);
      const cur = t ? norm(t.path) : '';
      const open = new Set([...tabs.values()].map((x) => norm(x.path)));
      ul.querySelectorAll('li.file').forEach((li) => {
        const p = norm(li.dataset.path);
        const row = li.firstElementChild;
        row.classList.toggle('current', p === cur);
        row.classList.toggle('open-doc', open.has(p));
      });
    }

    return {
      onDir, reveal, go, refresh, markOpen,
      get root() { return root; },
      get parent() { return parent; }
    };
  })();

  function syncTree() {
    if (pane !== 'files' || !sideOpen()) return;
    const t = tabs.get(active);
    if (t) tree.reveal(t.path, t.dir, false);
    else if (!tree.root) tree.go(store.get('treeRoot') || '');
  }

  $('treeUp').addEventListener('click', () => { if (tree.parent) tree.go(tree.parent); });
  $('treeRefresh').addEventListener('click', () => tree.refresh());
  $('treeExplore').addEventListener('click', () => { if (tree.root) send({ type: 'explore', path: tree.root }); });
  $('treeLocate').addEventListener('click', () => {
    const t = tabs.get(active);
    if (t) tree.reveal(t.path, t.dir, true);
  });

  function slugify(text, used) {
    const base = text.trim().toLowerCase()
      .replace(/[^\p{L}\p{N}\s_-]/gu, '')
      .replace(/\s/g, '-') || 'seccion';
    let slug = base, n = 1;
    while (used.has(slug)) slug = base + '-' + n++;
    used.add(slug);
    return slug;
  }

  function buildToc(el) {
    headings = el ? [...el.querySelectorAll('h1, h2, h3, h4, h5, h6')] : [];
    tocList.innerHTML = '';
    activeIdx = -1;
    for (const h of headings) {
      const li = document.createElement('li');
      li.className = 'l' + h.tagName[1];
      const a = document.createElement('a');
      a.href = '#' + encodeURIComponent(h.id);
      a.textContent = h.textContent;
      a.title = h.textContent;
      li.appendChild(a);
      tocList.appendChild(li);
    }
    $('tocEmpty').hidden = headings.length > 0;
    $('tocEmpty').textContent = el ? 'Este documento no tiene títulos.' : 'No hay ningún documento abierto.';
    markActive();
  }

  // Título activo del índice: búsqueda binaria (O(log n) lecturas de layout) y como mucho una vez por fotograma.
  let activeIdx = -1, scrollQueued = false;
  function markActive() {
    scrollQueued = false;
    if (!headings.length || pane !== 'index' || !sideOpen()) return;
    let lo = 0, hi = headings.length - 1, idx = 0;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (headings[mid].getBoundingClientRect().top < 100) { idx = mid; lo = mid + 1; } else hi = mid - 1;
    }
    if (idx === activeIdx) return;
    const items = tocList.children;
    if (items[activeIdx]) items[activeIdx].firstChild.classList.remove('active');
    if (items[idx]) items[idx].firstChild.classList.add('active');
    activeIdx = idx;
  }
  addEventListener('scroll', () => {
    if (!scrollQueued) { scrollQueued = true; requestAnimationFrame(markActive); }
  }, { passive: true });

  function scrollToId(id) {
    const t = tabs.get(active);
    if (!t) return;
    const el = t.el.querySelector('#' + CSS.escape(id)) || t.el.querySelector('[name="' + CSS.escape(id) + '"]');
    if (el) el.scrollIntoView({ block: 'start' });
  }

  /* ---------------- pestañas ---------------- */
  const X_ICON = '<svg viewBox="0 0 12 12"><path d="M3 3l6 6M9 3l-6 6"/></svg>';

  function ensureTab(id) {
    let t = tabs.get(id);
    if (t) return t;
    const el = document.createElement('article');
    el.className = 'md';
    el.hidden = true;
    view.appendChild(el);

    const tabEl = document.createElement('div');
    tabEl.className = 'tab';
    tabEl.setAttribute('role', 'tab');
    tabEl.innerHTML = '<span class="name"></span><button class="x" title="Cerrar (Ctrl+W)">' + X_ICON + '</button>';
    tabEl.addEventListener('mousedown', (e) => { if (e.button === 0 && !e.target.closest('.x')) activate(id); });
    tabEl.addEventListener('auxclick', (e) => { if (e.button === 1) { e.preventDefault(); closeTab(id); } });
    tabEl.querySelector('.x').addEventListener('click', (e) => { e.stopPropagation(); closeTab(id); });
    tabsBar.appendChild(tabEl);

    t = { id, host: '', name: '', path: '', el, tabEl, scroll: 0, canBack: false };
    tabs.set(id, t);
    return t;
  }

  function activate(id) {
    const t = tabs.get(id);
    if (!t) return;
    if (active !== null && active !== id) {
      const old = tabs.get(active);
      if (old) { old.scroll = scrollY; old.el.hidden = true; old.tabEl.classList.remove('active'); old.tabEl.setAttribute('aria-selected', 'false'); }
    }
    active = id;
    empty.hidden = true;
    t.el.hidden = false;
    t.tabEl.classList.add('active');
    t.tabEl.setAttribute('aria-selected', 'true');
    t.tabEl.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    buildToc(t.el);
    scrollTo(0, t.scroll);
    document.title = t.name;
    $('btnBack').hidden = !t.canBack;
    send({ type: 'activate', tab: id });
    tree.markOpen();
    syncTree();
  }

  function closeTab(id) {
    const t = tabs.get(id);
    if (!t) return;
    const order = [...tabs.keys()];
    const idx = order.indexOf(id);
    t.el.remove();
    t.tabEl.remove();
    tabs.delete(id);
    send({ type: 'closeTab', tab: id });
    if (active === id) {
      active = null;
      const next = order[idx + 1] ?? order[idx - 1];
      if (next !== undefined) activate(next); else showEmpty();
    }
    tree.markOpen();
  }

  function showEmpty() {
    active = null;
    empty.hidden = false;
    buildToc(null);
    $('btnBack').hidden = true;
    document.title = 'Lector MD';
    scrollTo(0, 0);
    send({ type: 'activate', tab: 0 });
  }

  function cycle(step) {
    const order = [...tabs.keys()];
    if (order.length < 2) return;
    const i = order.indexOf(active);
    activate(order[(i + step + order.length) % order.length]);
  }

  /* ---------------- render ---------------- */
  let highlighter = null;
  function loadHighlighter() {
    if (!highlighter) {
      highlighter = new Promise((resolve) => {
        const s = document.createElement('script');
        s.src = 'highlight.min.js';
        s.onload = resolve;
        s.onerror = resolve;
        document.head.appendChild(s);
      });
    }
    return highlighter;
  }

  // Lista blanca: solo lo que produce Markdown (y el HTML básico habitual en README).
  const PURIFY = {
    ALLOWED_TAGS: ['a', 'abbr', 'b', 'blockquote', 'br', 'caption', 'cite', 'code', 'col', 'colgroup', 'dd', 'del',
      'details', 'dfn', 'div', 'dl', 'dt', 'em', 'figcaption', 'figure', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'hr', 'i',
      'img', 'input', 'ins', 'kbd', 'li', 'mark', 'ol', 'p', 'pre', 'q', 's', 'samp', 'small', 'span', 'strong', 'sub',
      'summary', 'sup', 'table', 'tbody', 'td', 'tfoot', 'th', 'thead', 'tr', 'u', 'ul', 'var'],
    ALLOWED_ATTR: ['href', 'src', 'alt', 'title', 'width', 'height', 'align', 'colspan', 'rowspan', 'id', 'name',
      'class', 'start', 'type', 'checked', 'disabled', 'open', 'lang', 'dir'],
    ALLOW_DATA_ATTR: false
  };
  const PURIFY_FRAGMENT = Object.assign({ RETURN_DOM_FRAGMENT: true }, PURIFY);
  const allowedRemote = new Set(); // documentos cuyas imágenes externas ha aceptado el usuario (esta sesión)

  const ALERTS = { note: 'Nota', tip: 'Consejo', important: 'Importante', warning: 'Advertencia', caution: 'Precaución' };

  function postProcess(el, base, frontMatter, docPath) {
    if (frontMatter) {
      const d = document.createElement('details');
      d.className = 'frontmatter';
      d.innerHTML = '<summary>Metadatos</summary><pre></pre>';
      d.querySelector('pre').textContent = frontMatter;
      el.prepend(d);
    }

    // Avisos estilo GitHub: > [!NOTE]
    el.querySelectorAll('blockquote').forEach((bq) => {
      const p = bq.firstElementChild;
      if (!p || p.tagName !== 'P' || !p.firstChild || p.firstChild.nodeType !== 3) return;
      const m = p.firstChild.nodeValue.match(/^\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*/i);
      if (!m) return;
      const kind = m[1].toLowerCase();
      p.firstChild.nodeValue = p.firstChild.nodeValue.slice(m[0].length);
      if (p.firstChild.nodeValue === '' && p.childNodes[1] && p.childNodes[1].nodeName === 'BR') p.childNodes[1].remove();
      if (!p.textContent.trim() && !p.querySelector('img')) p.remove();
      bq.classList.add('alert', 'alert-' + kind);
      const t = document.createElement('div');
      t.className = 'alert-title';
      t.textContent = ALERTS[kind];
      bq.prepend(t);
    });

    // Solo casillas de tareas, y de solo lectura.
    el.querySelectorAll('input').forEach((i) => {
      if (i.type !== 'checkbox') i.remove(); else i.disabled = true;
    });

    // Imágenes: relativas → carpeta del documento. Solo https; nada de rutas con "\" (evita saltos a \\servidor).
    // Las externas no se cargan hasta que el usuario lo pide (privacidad: avisan al autor de que lo has abierto).
    const remote = [];
    el.querySelectorAll('img').forEach((img) => {
      const src = img.getAttribute('src') || '';
      let url = null;
      try { url = new URL(src, base); } catch { /* ruta inválida */ }
      const local = url && url.protocol === 'https:' && url.hostname.endsWith('.mdlector');
      const ok = url && ((url.protocol === 'https:' && !/%5c|\\/i.test(url.pathname)) ||
        (url.protocol === 'data:' && /^data:image\//i.test(url.href)));
      img.removeAttribute('src');
      if (!ok) return;
      img.loading = 'lazy';
      img.decoding = 'async';
      if (url.protocol === 'https:' && !local && !allowedRemote.has(docPath)) { remote.push([img, url.href]); return; }
      img.src = url.href;
    });
    if (remote.length) {
      const bar = document.createElement('div');
      bar.className = 'remote-note';
      bar.innerHTML = '<span></span><button type="button">Mostrar</button>';
      bar.firstChild.textContent = remote.length === 1
        ? 'Hay 1 imagen externa sin cargar (protege tu privacidad).'
        : 'Hay ' + remote.length + ' imágenes externas sin cargar (protege tu privacidad).';
      bar.lastChild.addEventListener('click', () => {
        allowedRemote.add(docPath);
        remote.forEach(([img, href]) => { img.src = href; });
        bar.remove();
      });
      el.prepend(bar);
    }

    // Resaltado de código (highlight.js se carga solo si hace falta) + botón copiar
    const langBlocks = el.querySelectorAll('pre > code[class*="language-"]');
    if (langBlocks.length) {
      loadHighlighter().then(() => langBlocks.forEach((code) => {
        try { hljs.highlightElement(code); } catch { /* lenguaje desconocido */ }
      }));
    }
    el.querySelectorAll('pre > code').forEach((code) => {
      const btn = document.createElement('button');
      btn.className = 'copy';
      btn.type = 'button';
      btn.textContent = 'Copiar';
      btn.addEventListener('click', async () => {
        try {
          await navigator.clipboard.writeText(code.innerText);
          btn.textContent = 'Copiado';
        } catch { btn.textContent = 'Error'; }
        setTimeout(() => { btn.textContent = 'Copiar'; }, 1400);
      });
      code.parentElement.appendChild(btn);
    });

    // Ids de títulos (para índice y enlaces #ancla)
    const used = new Set();
    el.querySelectorAll('h1, h2, h3, h4, h5, h6').forEach((h) => {
      if (!h.id) h.id = slugify(h.textContent, used); else used.add(h.id);
    });
  }

  function render(msg) {
    const t = ensureTab(msg.tab);
    const isActive = active === t.id;
    const ratio = msg.keepScroll && isActive ? scrollY / Math.max(1, root.scrollHeight - innerHeight) : null;

    let text = String(msg.text || '').replace(/^﻿/, '');
    let frontMatter = '';
    const fm = text.match(/^---\r?\n([\s\S]*?)\r?\n(?:---|\.\.\.)[ \t]*(?:\r?\n|$)/);
    if (fm) { frontMatter = fm[1]; text = text.slice(fm[0].length); }

    const navigated = t.path && t.path !== msg.path;
    t.base = msg.base;
    t.name = msg.name;
    t.path = msg.path;
    t.dir = msg.dir;
    t.canBack = !!msg.canBack;
    t.tabEl.querySelector('.name').textContent = msg.name;
    t.tabEl.title = msg.path;

    // Se sanea y procesa en un fragmento inerte (nada se descarga) y solo después se inserta en la página.
    const frag = DOMPurify.sanitize(marked.parse(text), PURIFY_FRAGMENT);
    postProcess(frag, msg.base, frontMatter, msg.path);
    t.el.replaceChildren(frag);
    if (!msg.keepScroll || navigated) t.scroll = 0;

    if (msg.activate || isActive) {
      activate(t.id);
      if (ratio !== null) {
        scrollTo(0, ratio * Math.max(1, root.scrollHeight - innerHeight));
        if (!msg.activate) toast('Actualizado');
      } else if (msg.hash) {
        requestAnimationFrame(() => scrollToId(msg.hash));
      }
    }
  }

  /* ---------------- enlaces ---------------- */
  function onLink(e, newTab) {
    const a = e.target.closest('a[href]');
    if (!a) return;
    e.preventDefault();
    const href = a.getAttribute('href');
    if (href.startsWith('#')) {
      if (newTab) return;
      scrollToId(decodeURIComponent(href.slice(1)));
      if (a.closest('#toc') && narrow()) setToc(false);
      return;
    }
    const t = tabs.get(active);
    if (!t) return;
    let abs;
    try { abs = new URL(href, t.base).href; } catch { return; }
    send({ type: 'link', tab: t.id, href: abs, newTab });
  }
  document.addEventListener('click', (e) => onLink(e, e.ctrlKey));
  document.addEventListener('auxclick', (e) => { if (e.button === 1) onLink(e, true); });
  document.addEventListener('mousedown', (e) => { if (e.button === 1 && e.target.closest('a[href]')) e.preventDefault(); });
  document.addEventListener('mouseup', (e) => {
    if (e.button === 3 && active !== null) { e.preventDefault(); send({ type: 'back', tab: active }); }
  });

  /* ---------------- teclado ---------------- */
  addEventListener('keydown', (e) => {
    const k = e.key.toLowerCase();
    if (e.ctrlKey && k === 'o') { e.preventDefault(); send({ type: 'open' }); }
    else if (e.ctrlKey && k === 'b') { e.preventDefault(); toggleToc(); }
    else if (k === 'f5' || (e.ctrlKey && k === 'r')) { e.preventDefault(); if (active !== null) send({ type: 'reload', tab: active }); }
    else if (e.altKey && k === 'arrowleft') { e.preventDefault(); if (active !== null) send({ type: 'back', tab: active }); }
    else if (e.ctrlKey && (k === 'w' || k === 'f4')) { e.preventDefault(); if (active !== null) closeTab(active); else send({ type: 'close' }); }
    else if (e.ctrlKey && k === 'tab') { e.preventDefault(); cycle(e.shiftKey ? -1 : 1); }
    else if (e.ctrlKey && k === 'pagedown') { e.preventDefault(); cycle(1); }
    else if (e.ctrlKey && k === 'pageup') { e.preventDefault(); cycle(-1); }
    else if (e.ctrlKey && !e.shiftKey && /^[1-9]$/.test(k)) {
      e.preventDefault();
      const order = [...tabs.keys()];
      const target = k === '9' ? order[order.length - 1] : order[Number(k) - 1];
      if (target !== undefined) activate(target);
    }
    else if (e.ctrlKey && e.shiftKey && k === 'e') { e.preventDefault(); if (active !== null) send({ type: 'folder', tab: active }); }
    else if (k === 'escape' && narrow() && document.body.classList.contains('toc-open')) setToc(false);
  });

  /* ---------------- varios ---------------- */
  $('btnNew').addEventListener('click', () => send({ type: 'open' }));
  $('btnOpenBig').addEventListener('click', () => send({ type: 'open' }));
  $('btnBack').addEventListener('click', () => { if (active !== null) send({ type: 'back', tab: active }); });
  $('bar').addEventListener('dblclick', (e) => { if (e.target === $('bar') || e.target === tabsBar) send({ type: 'open' }); });

  let dragDepth = 0;
  addEventListener('dragenter', () => { dragDepth++; document.body.classList.add('dragging'); });
  addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; document.body.classList.remove('dragging'); } });
  addEventListener('drop', () => { dragDepth = 0; document.body.classList.remove('dragging'); });

  let toastTimer;
  const toastEl = $('toast'); // referencia fija: un documento podría traer otro id="toast"
  function toast(text) {
    const el = toastEl;
    el.textContent = text;
    el.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.remove('show'), 1300);
  }

  if (host) {
    host.addEventListener('message', (e) => {
      const m = e.data;
      if (m && m.type === 'doc') render(m);
      else if (m && m.type === 'dir') tree.onDir(m);
    });
  }
  applyTheme();
  buildToc(null);
  setPane(pane, false);
  setToc(!narrow() && store.get('toc') !== '0', false);
  send({ type: 'ready' });
})();

