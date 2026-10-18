/* MatBill: the behaviour of the app itself. MatUi (wwwroot/ui/matui.js) does menus, dialogs, tabs, validation and pickers; this file adds what is specific to
   MatBill: the document editor (suggestions, positions, sums, the switch between document and form, a local copy of unsaved work), the installed app (service
   worker, colour of the status bar) and the behaviour without a connection. Everything is wired by data attributes; no inline styles anywhere (a value that is
   data is set here as a custom property, which the content security policy allows). */
(function () {
  'use strict';

  var doc = document;
  var root = doc.documentElement;
  var $ = function (sel, scope) { return (scope || doc).querySelector(sel); };
  var $$ = function (sel, scope) { return Array.prototype.slice.call((scope || doc).querySelectorAll(sel)); };
  var csrf = function () { var m = $('meta[name="mb-csrf"]'); return m ? m.getAttribute('content') : ''; };
  // The texts in the language of the page: the JSON block #mb-texts holds every key "Js.*" (Resources/Texts/Js.json). t('Js.Key', a, b) fills {0}, {1}.
  var texts = (function () { var el = doc.getElementById('mb-texts'); try { return el ? JSON.parse(el.textContent || '{}') : {}; } catch (e) { return {}; } })();
  var t = function (key) {
    var args = Array.prototype.slice.call(arguments, 1);
    var text = Object.prototype.hasOwnProperty.call(texts, key) ? texts[key] : key;
    return text.replace(/\{(\d+)\}/g, function (m, i) { return args[+i] !== undefined ? String(args[+i]) : m; });
  };
  var toast = function (message, tone) { if (window.MatUi && window.MatUi.toast) { window.MatUi.toast(message, { tone: tone || 'danger' }); } };

  function post(url, data) {
    return fetch(url, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'RequestVerificationToken': csrf(), 'Content-Type': 'application/x-www-form-urlencoded', 'X-Requested-With': 'fetch' },
      body: new URLSearchParams(data || {}).toString()
    }).then(function (response) {
      return response.json().catch(function () { return {}; }).then(function (body) {
        if (!response.ok) { throw new Error(body.error || 'Das hat nicht geklappt.'); }
        return body;
      });
    });
  }

  // ================================================================================================ the installed app

  // The colour of the status bar follows the theme the person chose in the app (the meta tags follow the system by themselves).
  function syncThemeColor() {
    var theme = root.getAttribute('data-theme');
    $$('meta[data-theme-color]').forEach(function (m) {
      if (!m.hasAttribute('data-media')) { m.setAttribute('data-media', m.getAttribute('media') || ''); }
      if (theme === 'light' || theme === 'dark') {
        if (m.getAttribute('data-theme-color') === theme) { m.setAttribute('media', 'all'); } else { m.setAttribute('media', 'not all'); }
      } else {
        m.setAttribute('media', m.getAttribute('data-media'));
      }
    });
  }
  syncThemeColor();
  root.addEventListener('mat:theme', syncThemeColor);

  var sw = navigator.serviceWorker;
  if (sw && root.getAttribute('data-sw')) {
    window.addEventListener('load', function () {
      sw.register('/sw.js?v=' + encodeURIComponent(root.getAttribute('data-sw')), { scope: '/' }).catch(function () { /* the app works without it */ });
    });
  }

  // The kept pages hold the data of the person who was signed in: they go with signing out, and on the sign-in page.
  function clearKeptPages() {
    if (sw && sw.controller) { sw.controller.postMessage({ type: 'clear-pages' }); }
    if (window.caches) { caches.delete('mb-pages').catch(function () { }); }
    try { Object.keys(localStorage).filter(function (k) { return k.indexOf('mb-draft:') === 0; }).forEach(function (k) { localStorage.removeItem(k); }); } catch (e) { /* ignore */ }
  }
  if (root.hasAttribute('data-clear-kept')) { clearKeptPages(); }
  doc.addEventListener('submit', function (e) {
    if (e.target && e.target.getAttribute && /\/Account\/Logout$/i.test(e.target.getAttribute('action') || '')) { clearKeptPages(); }
  }, true);

  // ================================================================================================ without a connection

  var banner = $('[data-offline-banner]');
  var copyFrom = root.getAttribute('data-offline-copy');
  var retryTimer = null;
  var retryDelay = 3000;

  function setOffline(offline) {
    root.classList.toggle('is-offline', offline);
    if (banner) { banner.hidden = !offline && copyFrom === null; }
  }

  function ping() {
    return fetch('/health', { cache: 'no-store', credentials: 'same-origin' }).then(function (r) { return r.ok; }).catch(function () { return false; });
  }

  function back() {
    clearTimeout(retryTimer);
    retryDelay = 3000;
    setOffline(false);
    // A copy that was shown without a connection is replaced by the real page: nothing stale stays on the screen.
    if (copyFrom !== null || $('[data-offline-page]')) { location.reload(); return; }
    toast('Die Verbindung ist wieder da.', 'success');
    refreshBell();
  }

  function scheduleRetry() {
    clearTimeout(retryTimer);
    retryTimer = setTimeout(function () {
      ping().then(function (ok) {
        if (ok) { back(); } else { retryDelay = Math.min(retryDelay * 2, 60000); scheduleRetry(); }
      });
    }, retryDelay);
  }

  function reconnectNow(button) {
    var status = $('[data-offline-status]');
    if (button) { button.disabled = true; }
    if (status) { status.textContent = 'Verbinde ...'; }
    ping().then(function (ok) {
      if (button) { button.disabled = false; }
      if (ok) { back(); return; }
      if (status) { status.textContent = 'Noch keine Verbindung. Es wird automatisch weiter versucht.'; }
      toast('Noch keine Verbindung.', 'warning');
      scheduleRetry();
    });
  }

  window.addEventListener('offline', function () { setOffline(true); scheduleRetry(); });
  window.addEventListener('online', function () { ping().then(function (ok) { if (ok) { back(); } else { scheduleRetry(); } }); });
  doc.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('[data-reconnect]');
    if (b) { e.preventDefault(); reconnectNow(b); }
  });

  if (copyFrom !== null) {
    var text = $('[data-offline-text]');
    var when = copyFrom ? new Date(copyFrom) : null;
    if (text && when && !isNaN(when)) {
      text.textContent = 'Du siehst den Stand vom ' + when.toLocaleDateString('de-DE') + ', ' + when.toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' }) + ' Uhr. Speichern ist möglich, sobald die Verbindung zurück ist.';
    }
    setOffline(true);
    scheduleRetry();
  } else if (!navigator.onLine) {
    setOffline(true);
    scheduleRetry();
  }
  if ($('[data-offline-page]')) { scheduleRetry(); }

  // A form that is sent without a connection would end in the browser's error page and lose what was typed: keep the page, say why.
  doc.addEventListener('submit', function (e) {
    var form = e.target;
    if (!form || (form.method || '').toLowerCase() !== 'post') { return; }
    if (!navigator.onLine || root.classList.contains('is-offline')) {
      e.preventDefault();
      e.stopImmediatePropagation();
      toast('Keine Verbindung: Es wurde nichts gespeichert. Deine Eingaben bleiben auf der Seite.', 'warning');
      scheduleRetry();
    }
  }, true);

  // ================================================================================================ the bell

  function refreshBell() {
    var bell = $('.mb-bell');
    if (!bell || doc.hidden || !navigator.onLine) { return; }
    fetch('/api/notifications/count', { credentials: 'same-origin', cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (data) {
      if (!data) { return; }
      var badge = $('.mb-bell__count', bell);
      if (data.unread > 0) {
        if (!badge) { badge = doc.createElement('span'); badge.className = 'mat-badge mb-bell__count'; badge.setAttribute('aria-hidden', 'true'); bell.appendChild(badge); }
        badge.textContent = data.unread > 99 ? '99+' : String(data.unread);
        bell.setAttribute('aria-label', 'Benachrichtigungen, ' + data.unread + ' ungelesen');
      } else if (badge) {
        badge.parentNode.removeChild(badge);
        bell.setAttribute('aria-label', 'Benachrichtigungen');
      }
    }).catch(function () { });
  }
  setInterval(refreshBell, 120000);
  doc.addEventListener('visibilitychange', function () { if (!doc.hidden) { refreshBell(); } });

  // ================================================================================================ small helpers of forms

  // <select data-submit-on-change> sends its form when it changes.
  doc.addEventListener('change', function (event) {
    var t = event.target;
    if (t && t.hasAttribute && t.hasAttribute('data-submit-on-change') && t.form) { t.form.requestSubmit ? t.form.requestSubmit() : t.form.submit(); }
  });

  // <button data-prompt="Frage" data-prompt-into="Name"> asks for a text, writes it into the hidden field and sends the form with this button.
  doc.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('button[data-prompt]');
    if (!b || !window.MatUi || !window.MatUi.prompt) { return; }
    e.preventDefault();
    window.MatUi.prompt(b.getAttribute('data-prompt'), { title: b.getAttribute('data-prompt-title') || undefined, ok: b.getAttribute('data-prompt-ok') || undefined }).then(function (answer) {
      if (answer === null || answer === undefined || answer === false) { return; }
      var form = b.form;
      var field = form && form.querySelector('[name="' + b.getAttribute('data-prompt-into') + '"]');
      if (field) { field.value = answer; }
      if (form) { form.requestSubmit ? form.requestSubmit(b) : form.submit(); }
    });
  });

  // ================================================================================================ the document editor

  var editor = $('[data-editor]');
  if (editor) { initEditor(editor); }

  function initEditor(editor) {
    var form = editor.closest('form') || $('form', editor);
    var dataEl = $('#mb-editor-data');
    var data = dataEl ? JSON.parse(dataEl.textContent || '{}') : {};
    var customers = data.customers || [];
    var products = data.products || [];
    var readOnly = editor.hasAttribute('data-readonly');
    var money = new Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR' });
    var number = function (v, digits) { return new Intl.NumberFormat('de-DE', { minimumFractionDigits: digits, maximumFractionDigits: digits }).format(v); };
    var quantity = function (v) { return new Intl.NumberFormat('de-DE', { maximumFractionDigits: 3 }).format(v); };

    // ---- the colour of the document (branding) is data: a custom property
    var accent = editor.getAttribute('data-doc-accent');
    if (accent && /^#[0-9a-f]{6}$/i.test(accent)) { editor.style.setProperty('--mb-doc-accent', accent); }

    // ---- document or form
    function setView(view, save) {
      editor.setAttribute('data-view', view);
      $$('[data-view-set]').forEach(function (b) { b.setAttribute('aria-pressed', b.getAttribute('data-view-set') === view ? 'true' : 'false'); });
      $$('textarea[data-autogrow]', editor).forEach(grow);
      try { localStorage.setItem('mb-editor-view', view); } catch (e) { /* ignore */ }
      if (save) { post('/api/editor-view', { view: view }).catch(function () { }); }
    }
    $$('[data-view-set]').forEach(function (b) {
      b.addEventListener('click', function () { setView(b.getAttribute('data-view-set'), true); });
    });
    setView(editor.getAttribute('data-view') || 'document', false);

    // ---- text areas grow with their text (a document has no scroll boxes)
    function grow(t) {
      if (editor.getAttribute('data-view') === 'form') { t.style.removeProperty('height'); return; }
      t.style.height = 'auto';
      t.style.height = t.scrollHeight + 2 + 'px';
    }
    $$('textarea[data-autogrow]', editor).forEach(grow);
    editor.addEventListener('input', function (e) { if (e.target.matches && e.target.matches('textarea[data-autogrow]')) { grow(e.target); } });
    window.addEventListener('resize', function () { $$('textarea[data-autogrow]', editor).forEach(grow); });

    if (readOnly) { return; }

    // ---- amounts as people type them
    function parseAmount(text) {
      if (text === null || text === undefined) { return NaN; }
      var s = String(text).replace(/[\s €%']/g, '');
      if (!s) { return NaN; }
      var dot = s.lastIndexOf('.'), comma = s.lastIndexOf(',');
      if (dot >= 0 && comma >= 0) {
        var dec = dot > comma ? '.' : ',';
        s = s.split(dec === '.' ? ',' : '.').join('').replace(dec, '.');
      } else if (comma >= 0) {
        if (s.split(',').length > 2) { return NaN; }
        s = s.replace(',', '.');
      } else if (dot >= 0) {
        var groups = s.split('.');
        var head = groups[0].replace('-', '');
        var grouping = groups.slice(1).every(function (g) { return g.length === 3; }) && (groups.length > 2 || (head.length > 0 && head.length <= 3 && head !== '0'));
        if (grouping) { s = groups.join(''); } else if (groups.length > 2) { return NaN; }
      }
      return /^-?\d*\.?\d+$/.test(s) ? parseFloat(s) : NaN;
    }
    var round2 = function (v) { return Math.round((v + (v >= 0 ? Number.EPSILON : -Number.EPSILON)) * 100) / 100; };

    // ---- positions
    var body = $('[data-lines-body]', editor);
    var template = $('template[data-line-template]', editor);
    var empty = $('[data-lines-empty]', editor);

    function lines() { return $$('[data-line]', body); }

    // Every field of a position is named after its place (Input.Lines[2].Name): the order on the page is the order that is saved.
    function renumber() {
      lines().forEach(function (line, i) {
        $$('[data-field]', line).forEach(function (input) {
          var field = input.getAttribute('data-field');
          var id = 'line-' + i + '-' + field;
          input.name = 'Input.Lines[' + i + '].' + field;
          input.id = id;
          var label = $('label[data-for="' + field + '"]', line);
          if (label) { label.setAttribute('for', id); label.textContent = label.getAttribute('data-text').replace('{n}', i + 1); }
        });
        var list = $('.mb-combo__list', line);
        if (list) { list.id = 'line-' + i + '-Name-list'; var n = $('[data-field="Name"]', line); if (n) { n.setAttribute('aria-controls', list.id); } }
        var menu = $('.mb-line__menu', line);
        var more = $('[data-line-more]', line);
        if (menu && more) { menu.id = 'line-' + i + '-menu'; more.setAttribute('aria-controls', menu.id); }
        line.setAttribute('aria-label', 'Position ' + (i + 1));
      });
      if (empty) { empty.hidden = lines().length > 0; }
    }

    function addLine(after, focus) {
      var line = template.content.firstElementChild.cloneNode(true);
      if (after && after.parentNode === body) { body.insertBefore(line, after.nextSibling); } else { body.appendChild(line); }
      var tax = $('[data-field="TaxRate"]', line);
      if (tax && data.defaultTaxRate !== undefined) { tax.value = String(data.defaultTaxRate); }
      renumber();
      wireLine(line);
      recalc();
      if (focus !== false) { var n = $('[data-field="Name"]', line); if (n) { n.focus(); } }
      return line;
    }

    function wireLine(line) {
      var name = $('[data-field="Name"]', line);
      if (name && !name._combo) {
        name._combo = combo(name, {
          source: function (q) {
            return filter(products, q, function (p) { return [p.name, p.description || '']; }).slice(0, 8).map(function (p) {
              return { name: p.name, desc: p.description, end: p.unitPrice + ' €' + (p.unit ? ' / ' + p.unit : ''), icon: 'box', data: p };
            });
          },
          emptyHint: products.length === 0 ? 'Noch keine Produkte angelegt: einfach die Leistung eintragen.' : null,
          onSelect: function (item) { fillLine(line, item.data); },
          onType: function () { var pid = $('[data-field="ProductId"]', line); if (pid) { pid.value = ''; } }
        });
      }
    }

    function fillLine(line, p) {
      var set = function (field, value) { var el = $('[data-field="' + field + '"]', line); if (el) { el.value = value === null || value === undefined ? '' : value; if (el.tagName === 'TEXTAREA') { grow(el); } } };
      set('ProductId', p.id);
      set('Name', p.name);
      set('Description', p.description);
      set('Unit', p.unit);
      set('UnitPrice', p.unitPrice);
      var tax = $('[data-field="TaxRate"]', line);
      if (tax) { tax.value = p.taxRate; if (tax.value !== p.taxRate) { tax.value = '19'; } }
      var qty = $('[data-field="Quantity"]', line);
      if (qty && !qty.value) { qty.value = '1'; }
      recalc();
      markDirty();
      if (qty) { qty.focus(); qty.select(); }
    }

    function closeMenus(except) { $$('.mb-line__menu', body).forEach(function (m) { if (m !== except) { m.hidden = true; var b = $('[data-line-more]', m.closest('[data-line]')); if (b) { b.setAttribute('aria-expanded', 'false'); } } }); }

    body.addEventListener('click', function (e) {
      var line = e.target.closest('[data-line]');
      if (!line) { return; }
      var more = e.target.closest('[data-line-more]');
      if (more) {
        var menu = $('.mb-line__menu', line);
        var open = menu.hidden;
        closeMenus(menu);
        menu.hidden = !open;
        more.setAttribute('aria-expanded', open ? 'true' : 'false');
        if (open) { var first = $('button', menu); if (first) { first.focus(); } }
        return;
      }
      var action = e.target.closest('[data-line-action]');
      if (!action) { return; }
      var what = action.getAttribute('data-line-action');
      closeMenus();
      if (what === 'remove') {
        var next = line.nextElementSibling || line.previousElementSibling;
        line.parentNode.removeChild(line);
        renumber(); recalc(); markDirty();
        var focus = next ? $('[data-field="Name"]', next) : $('[data-line-add]', editor);
        if (focus) { focus.focus(); }
        toast('Position entfernt.', 'success');
      } else if (what === 'up' && line.previousElementSibling) {
        body.insertBefore(line, line.previousElementSibling); renumber(); markDirty(); $('[data-line-more]', line).focus();
      } else if (what === 'down' && line.nextElementSibling) {
        body.insertBefore(line.nextElementSibling, line); renumber(); markDirty(); $('[data-line-more]', line).focus();
      } else if (what === 'copy') {
        var copy = addLine(line, false);
        $$('[data-field]', line).forEach(function (src) { var dst = $('[data-field="' + src.getAttribute('data-field') + '"]', copy); if (dst) { dst.value = src.value; } });
        $$('textarea[data-autogrow]', copy).forEach(grow);
        recalc(); markDirty();
        $('[data-field="Name"]', copy).focus();
      }
    });
    doc.addEventListener('click', function (e) { if (!e.target.closest || !e.target.closest('.mb-line__more')) { closeMenus(); } });
    body.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') { var m = e.target.closest('.mb-line__menu'); if (m) { closeMenus(); var b = $('[data-line-more]', m.closest('[data-line]')); if (b) { b.focus(); } } }
      // Enter in the last field of the last position starts the next one (as on paper: write on).
      if (e.key === 'Enter' && e.target.matches('[data-field="UnitPrice"]') && e.target.closest('[data-line]') === lines()[lines().length - 1]) { e.preventDefault(); addLine(); }
    });
    var add = $('[data-line-add]', editor);
    if (add) { add.addEventListener('click', function () { addLine(); }); }
    lines().forEach(wireLine);
    renumber();

    // ---- the sums, the same arithmetic as the server (InvoiceMath): a position is rounded to cents, the tax once per rate
    function recalc() {
      var groups = {};
      var net = 0;
      lines().forEach(function (line) {
        var qtyField = $('[data-field="Quantity"]', line);
        var q = parseAmount(qtyField ? qtyField.value : '1');
        if (isNaN(q)) { q = qtyField && qtyField.value.trim() === '' ? 1 : 0; }
        var p = parseAmount($('[data-field="UnitPrice"]', line).value);
        if (isNaN(p)) { p = 0; }
        var rate = data.taxExempt ? 0 : parseFloat(($('[data-field="TaxRate"]', line) || { value: '0' }).value) || 0;
        var lineNet = round2(q * p);
        var out = $('[data-line-total]', line);
        if (out) { out.textContent = money.format(lineNet); }
        groups[rate] = round2((groups[rate] || 0) + lineNet);
        net = round2(net + lineNet);
      });
      var rates = Object.keys(groups).map(parseFloat).filter(function (r) { return r > 0; }).sort(function (a, b) { return b - a; });
      var tax = 0;
      var rows = $('[data-tax-rows]', editor);
      if (rows) {
        rows.innerHTML = '';
        rates.forEach(function (r) {
          var t = round2(groups[r] * r / 100);
          tax = round2(tax + t);
          var row = doc.createElement('div');
          var dt = doc.createElement('dt');
          dt.textContent = rates.length > 1 ? 'Umsatzsteuer ' + quantity(r) + ' % auf ' + money.format(groups[r]) : 'Umsatzsteuer (' + quantity(r) + ' %)';
          var dd = doc.createElement('dd');
          dd.textContent = money.format(t);
          row.appendChild(dt); row.appendChild(dd); rows.appendChild(row);
        });
      }
      var setText = function (sel, value) { var el = $(sel, editor); if (el) { el.textContent = value; } };
      setText('[data-sum-net]', money.format(net));
      setText('[data-sum-gross]', money.format(round2(net + tax)));
      updateDue();
    }

    function updateDue() {
      var out = $('[data-due]', editor);
      var date = $('[name="Input.InvoiceDate"]', editor);
      var days = $('[name="Input.PaymentTermDays"]', editor);
      if (!out || !date || !days) { return; }
      var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date.value || '');
      var n = parseInt(days.value, 10);
      if (!m || isNaN(n)) { out.textContent = '–'; return; }
      var d = new Date(+m[1], +m[2] - 1, +m[3] + Math.max(0, n));
      out.textContent = d.toLocaleDateString('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric' });
      $$('[data-term-days]', editor).forEach(function (el) { el.textContent = String(Math.max(0, n)); });
    }

    editor.addEventListener('input', function (e) {
      if (e.target.closest('[data-line]') || e.target.matches('[name="Input.PaymentTermDays"]')) { recalc(); }
      markDirty();
    });
    editor.addEventListener('change', function (e) {
      if (e.target.closest('[data-line]') || e.target.matches('[name="Input.InvoiceDate"], [name="Input.PaymentTermDays"]')) { recalc(); }
      // An amount is written back the way the document shows it ("1200" becomes "1.200,00").
      if (e.target.matches('[data-field="UnitPrice"]')) { var v = parseAmount(e.target.value); if (!isNaN(v)) { e.target.value = number(v, 2); } }
      if (e.target.matches('[data-field="Quantity"]')) { var q = parseAmount(e.target.value); if (!isNaN(q)) { e.target.value = quantity(q); } }
      markDirty();
    });
    recalc();

    // ---- the recipient: suggestions from the customers, or a new customer from the name
    var recipient = $('[name="Input.RecipientName"]', editor);
    var customerId = $('[name="Input.CustomerId"]', editor);
    var newHint = $('[data-new-customer-hint]', editor);
    function fillRecipient(c) {
      var set = function (name, value) { var el = $('[name="Input.' + name + '"]', editor); if (el) { el.value = value || ''; } };
      if (customerId) { customerId.value = c.id; }
      set('RecipientName', c.name);
      set('RecipientContact', c.contact);
      set('RecipientStreet', c.street);
      set('RecipientPostalCode', c.postalCode);
      set('RecipientCity', c.city);
      set('RecipientCountry', c.country);
      set('RecipientEmail', c.email);
      if (c.paymentTermDays !== null && c.paymentTermDays !== undefined) { set('PaymentTermDays', c.paymentTermDays); }
      if (newHint) { newHint.hidden = true; }
      updateDue();
      markDirty();
      var next = $('[name="Input.RecipientContact"]', editor);
      if (next) { next.focus(); }
    }
    if (recipient) {
      combo(recipient, {
        source: function (q) {
          return filter(customers, q, function (c) { return [c.name, c.number, c.city || '', c.contact || '']; }).slice(0, 8).map(function (c) {
            return { name: c.name, desc: [c.number, [c.street, [c.postalCode, c.city].filter(Boolean).join(' ')].filter(Boolean).join(', ')].filter(Boolean).join(' · '), icon: 'building', data: c };
          });
        },
        create: data.canCreateCustomer === false ? null : function (q) { return 'Neuen Kunden „' + q + '“ anlegen'; },
        emptyHint: customers.length === 0 ? 'Noch keine Kunden: tippe einen Namen, um einen anzulegen.' : null,
        onSelect: function (item) { fillRecipient(item.data); },
        onCreate: function (q) {
          return post('/api/customers', { name: q }).then(function (answer) {
            customers.push(answer.customer);
            fillRecipient(answer.customer);
            toast('Kunde „' + answer.customer.name + '“ angelegt.', 'success');
          }).catch(function (err) { toast(err.message); });
        },
        onType: function () {
          if (customerId) { customerId.value = ''; }
          if (newHint) { newHint.hidden = !recipient.value.trim() || data.requireCustomer === true; }
        }
      });
    }

    // ---- unsaved work: a warning before leaving, and a copy in this browser that survives a reload or a lost connection
    var key = data.draftKey ? 'mb-draft:' + data.draftKey : null;
    var dirty = false;
    var saveTimer = null;
    function snapshot() {
      renumber();
      var values = {};
      $$('input[name], textarea[name], select[name]', form).forEach(function (el) {
        if (el.type === 'hidden' && el.name === '__RequestVerificationToken') { return; }
        if (el.type === 'checkbox' || el.type === 'radio') { if (el.checked) { values[el.name] = el.value; } return; }
        values[el.name] = el.value;
      });
      return { at: Date.now(), lines: lines().length, values: values };
    }
    function markDirty() {
      dirty = true;
      if (!key) { return; }
      clearTimeout(saveTimer);
      saveTimer = setTimeout(function () { try { localStorage.setItem(key, JSON.stringify(snapshot())); } catch (e) { /* full or blocked: the warning still works */ } }, 600);
    }
    window.addEventListener('beforeunload', function (e) { if (dirty) { e.preventDefault(); e.returnValue = ''; } });
    form.addEventListener('submit', function (e) {
      if (e.defaultPrevented) { return; }
      renumber();
      dirty = false;
      clearTimeout(saveTimer);
      if (key) { try { localStorage.removeItem(key); } catch (err) { /* ignore */ } }
    });

    // A copy that is newer than what the server has: offer it back.
    var restore = $('[data-restore]', editor);
    if (key && restore) {
      var saved = null;
      try { saved = JSON.parse(localStorage.getItem(key) || 'null'); } catch (e) { saved = null; }
      if (saved && saved.at > (data.savedAt || 0)) {
        var when = new Date(saved.at);
        $('[data-restore-when]', restore).textContent = when.toLocaleDateString('de-DE') + ', ' + when.toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' }) + ' Uhr';
        restore.hidden = false;
        $('[data-restore-apply]', restore).addEventListener('click', function () {
          while (lines().length < saved.lines) { addLine(null, false); }
          while (lines().length > saved.lines) { body.removeChild(lines()[lines().length - 1]); }
          renumber();
          Object.keys(saved.values).forEach(function (name) {
            var el = form.querySelector('[name="' + CSS.escape(name) + '"]');
            if (!el) { return; }
            if (el.type === 'checkbox') { el.checked = saved.values[name] === el.value; } else { el.value = saved.values[name]; }
            if (el.classList.contains('mat-picker__input') && window.MatUi && window.MatUi.picker) { window.MatUi.picker.set(el.closest('.mat-picker'), el.value); }
          });
          $$('textarea[data-autogrow]', editor).forEach(grow);
          if (window.MatUi && window.MatUi.refresh) { window.MatUi.refresh(form); }
          recalc();
          dirty = true;
          restore.hidden = true;
          toast('Die ungespeicherten Eingaben sind wieder da. Speichere, um sie zu behalten.', 'success');
        });
        $('[data-restore-discard]', restore).addEventListener('click', function () {
          try { localStorage.removeItem(key); } catch (e) { /* ignore */ }
          restore.hidden = true;
        });
      } else if (saved) {
        try { localStorage.removeItem(key); } catch (e) { /* ignore */ }
      }
    }
  }

  // ================================================================================================ suggestions under a field (ARIA combobox)

  function fold(s) { return String(s || '').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, ''); }

  function filter(items, query, fields) {
    var q = fold(query).trim();
    if (!q) { return items.slice(); }
    var words = q.split(/\s+/);
    return items.filter(function (item) {
      var hay = fold(fields(item).join(' '));
      return words.every(function (w) { return hay.indexOf(w) !== -1; });
    }).sort(function (a, b) { return (fold(fields(a)[0]).indexOf(q) === 0 ? 0 : 1) - (fold(fields(b)[0]).indexOf(q) === 0 ? 0 : 1); });
  }

  var comboCount = 0;
  function combo(input, o) {
    var wrap = input.closest('.mb-combo') || input.parentNode;
    var list = doc.createElement('ul');
    list.className = 'mb-combo__list';
    list.setAttribute('role', 'listbox');
    list.id = (input.id || 'combo-' + (++comboCount)) + '-list';
    list.hidden = true;
    wrap.appendChild(list);
    input.setAttribute('role', 'combobox');
    input.setAttribute('aria-autocomplete', 'list');
    input.setAttribute('aria-expanded', 'false');
    input.setAttribute('aria-controls', list.id);
    input.setAttribute('autocomplete', 'off');
    var items = [];
    var active = -1;
    var typed = false;

    function render() {
      var q = input.value.trim();
      items = o.source(q);
      var exact = items.some(function (i) { return fold(i.name) === fold(q); });
      if (o.create && q && !exact) { items.push({ create: true, name: o.create(q), query: q, icon: 'plus' }); }
      list.innerHTML = '';
      if (items.length === 0) {
        if (o.emptyHint && !q) { var hint = doc.createElement('li'); hint.className = 'mb-combo__hint'; hint.textContent = o.emptyHint; list.appendChild(hint); open(true); }
        else { close(); }
        return;
      }
      items.forEach(function (item, i) {
        var li = doc.createElement('li');
        li.className = 'mb-combo__option' + (item.create ? ' mb-combo__option--create' : '');
        li.setAttribute('role', 'option');
        li.id = list.id + '-' + i;
        li.innerHTML = '<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-' + item.icon + '"></use></svg>';
        var text = doc.createElement('span'); text.className = 'mb-combo__text';
        var name = doc.createElement('span'); name.className = 'mb-combo__name'; name.textContent = item.name; text.appendChild(name);
        if (item.desc) { var desc = doc.createElement('span'); desc.className = 'mb-combo__desc'; desc.textContent = item.desc; text.appendChild(desc); }
        li.appendChild(text);
        if (item.end) { var end = doc.createElement('span'); end.className = 'mb-combo__end'; end.textContent = item.end; li.appendChild(end); }
        li.addEventListener('mousedown', function (e) { e.preventDefault(); choose(i); });
        list.appendChild(li);
      });
      active = -1;
      open(true);
    }

    function open(on) { list.hidden = !on; input.setAttribute('aria-expanded', on ? 'true' : 'false'); if (!on) { input.removeAttribute('aria-activedescendant'); } }
    function close() { open(false); active = -1; }

    function highlight(i) {
      var options = $$('.mb-combo__option', list);
      if (options.length === 0) { return; }
      active = (i + options.length) % options.length;
      options.forEach(function (el, n) { el.classList.toggle('is-active', n === active); el.setAttribute('aria-selected', n === active ? 'true' : 'false'); });
      input.setAttribute('aria-activedescendant', options[active].id);
      options[active].scrollIntoView({ block: 'nearest' });
    }

    function choose(i) {
      var item = items[i];
      close();
      typed = false;
      if (!item) { return; }
      if (item.create) { if (o.onCreate) { o.onCreate(item.query); } return; }
      if (o.onSelect) { o.onSelect(item); }
    }

    input.addEventListener('input', function () { typed = true; if (o.onType) { o.onType(); } render(); });
    input.addEventListener('focus', function () { if (!input.value.trim() || typed) { render(); } });
    input.addEventListener('blur', function () { setTimeout(close, 120); });
    input.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown') { e.preventDefault(); if (list.hidden) { render(); } highlight(active + 1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); if (!list.hidden) { highlight(active - 1); } }
      else if (e.key === 'Enter') { if (!list.hidden && active >= 0) { e.preventDefault(); choose(active); } }
      else if (e.key === 'Escape') { if (!list.hidden) { e.preventDefault(); e.stopPropagation(); close(); } }
      else if (e.key === 'Tab') { close(); }
    });
    return { close: close, render: render };
  }
})();

// Scrollbars are only visible while something scrolls: the class is set on the scrolled element and removed a moment later.
(function () {
  'use strict';
  var timers = new WeakMap();
  document.addEventListener('scroll', function (event) {
    var el = event.target === document ? document.documentElement : event.target;
    if (!el || !el.classList) { return; }
    el.classList.add('is-scrolling');
    clearTimeout(timers.get(el));
    timers.set(el, setTimeout(function () { el.classList.remove('is-scrolling'); }, 900));
  }, true);
})();

// Receipts that are dropped (or chosen, or pasted) on a drop zone with data-upload-autosubmit go up at once.
document.addEventListener('mat:upload-change', function (event) {
  'use strict';
  var input = event.target && event.target.querySelector && event.target.querySelector('input[data-upload-autosubmit]');
  if (!input || !input.files || input.files.length === 0 || !input.form) { return; }
  input.form.classList.add('is-busy');
  if (window.MatUi && window.MatUi.toast) { window.MatUi.toast(input.files.length === 1 ? 'Der Beleg wird hochgeladen …' : input.files.length + ' Belege werden hochgeladen …', { tone: 'info' }); }
  input.form.requestSubmit ? input.form.requestSubmit() : input.form.submit();
});
