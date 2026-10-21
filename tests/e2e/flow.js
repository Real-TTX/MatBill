// The way through MatBill in a real browser, against a running stack (./scripts/redeploy.ps1 -Fresh first, or any stack whose administrator is known):
//   cd tests/e2e && npm install && node flow.js http://localhost:2505
// Sets the site up (or signs in), writes the sample data, writes an invoice on the document (customer suggestion, product suggestion, sums), switches to the form,
// saves and issues it, sets up a repetition, and looks at every main page on a desktop and a phone. SHOTS=<folder> keeps screenshots.
// It fails on any error in the console of the browser (a script error, a blocked inline style or script) and on any step that does not do what it should.
const { chromium } = require('playwright-core');
const path = require('path');
const fs = require('fs');

const base = (process.argv[2] || 'http://localhost:2505').replace(/\/$/, '');
const shots = process.env.SHOTS || '';
const email = process.env.MB_EMAIL || 'admin@example.com';
const password = process.env.MB_PASSWORD || 'Start12345';
const problems = [];

function check(condition, message) {
  if (!condition) { problems.push(message); console.log('  FAIL ' + message); } else { console.log('  ok   ' + message); }
}

async function shot(page, name) {
  if (!shots) { return; }
  fs.mkdirSync(shots, { recursive: true });
  await page.screenshot({ path: path.join(shots, name + '.png'), fullPage: true });
}

async function confirmDialog(page) {
  const dialog = page.locator('dialog[open]').last();
  await dialog.waitFor({ state: 'visible', timeout: 5000 });
  // the confirming button is the primary or danger one of the dialog
  await dialog.locator('.mat-btn--primary, .mat-btn--danger').last().click();
}

(async () => {
  const browser = await chromium.launch({ channel: process.env.BROWSER || 'msedge', headless: true });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, locale: 'de-DE', timezoneId: 'Europe/Berlin' });
  const page = await context.newPage();
  page.on('console', msg => { if (msg.type() === 'error') { problems.push('console: ' + msg.text()); console.log('  console error: ' + msg.text()); } });
  page.on('pageerror', err => { problems.push('page error: ' + err.message); console.log('  page error: ' + err.message); });

  console.log('Setup or sign-in');
  await page.goto(base + '/');
  if (page.url().includes('/Account/Setup')) {
    await page.fill('#Input_DisplayName', 'Jonas Dierksen');
    await page.fill('#Input_Email', email);
    await page.fill('#Input_Password', password);
    await page.fill('#Input_PasswordRepeat', password);
    await page.click('form button[type=submit]');
    await page.waitForLoadState('networkidle');
    check(page.url().includes('/Company'), 'the setup ends on the company page');
  } else if (page.url().includes('/Account/Login')) {
    await page.fill('#Input_Email', email);
    await page.fill('#Input_Password', password);
    await page.click('form button[type=submit]');
    await page.waitForLoadState('networkidle');
  }
  check(!page.url().includes('/Account/'), 'signed in');

  console.log('Sample data');
  await page.goto(base + '/Admin/System');
  const demo = page.locator('button', { hasText: 'Beispieldaten anlegen' });
  if (await demo.count() > 0) {
    await demo.click();
    await confirmDialog(page);
    await page.waitForURL(u => !u.toString().includes('/Admin/System'), { timeout: 30000 });
  }
  await page.goto(base + '/');
  check(await page.locator('h1', { hasText: 'Übersicht' }).count() === 1, 'the overview opens');
  check(await page.locator('.mb-stat').count() === 5, 'five number cards on the overview');
  await shot(page, 'dashboard');

  console.log('A new invoice on the document');
  await page.goto(base + '/Invoices/Edit');
  check(await page.locator('.mb-paper').count() === 1, 'the editor shows the paper');
  check((await page.locator('[data-editor]').getAttribute('data-view')) === 'document', 'the editor opens as a document');
  await page.click('#Input_RecipientName');
  await page.keyboard.type('Nord');
  const option = page.locator('#Input_RecipientName-list .mb-combo__option').first();
  await option.waitFor({ timeout: 3000 });
  check((await option.innerText()).includes('Studio Nord'), 'the recipient suggests the customer');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  check((await page.inputValue('#Input_RecipientCity')) === 'Mannheim', 'choosing the customer fills the address');
  check((await page.inputValue('[name="Input.CustomerId"]')) !== '', 'choosing the customer sets the customer');

  const name = page.locator('[data-line] [data-field="Name"]').first();
  await name.click();
  await page.keyboard.type('Webd');
  await page.locator('[data-line] .mb-combo__option').first().waitFor({ timeout: 3000 });
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  check((await page.locator('[data-line] [data-field="UnitPrice"]').first().inputValue()) === '1.200,00', 'choosing a product fills the price');
  await page.locator('[data-line] [data-field="Quantity"]').first().fill('2');
  await page.locator('[data-line] [data-field="Quantity"]').first().dispatchEvent('input');
  const gross = (await page.locator('[data-sum-gross]').innerText()).replace(/\s/g, ' ');
  check(gross.includes('2.856,00'), 'the sums follow the positions (2 x 1.200 + 19 % = 2.856,00), got ' + gross);

  await page.click('[data-line-add]');
  check(await page.locator('[data-line]').count() === 2, 'a second position is added');
  await page.keyboard.type('Beratung vor Ort');
  await page.locator('[data-line]').nth(1).locator('[data-field="UnitPrice"]').fill('150');
  await page.locator('[data-line]').nth(1).locator('[data-field="UnitPrice"]').dispatchEvent('change');
  check((await page.locator('[data-line]').nth(1).locator('[data-field="UnitPrice"]').inputValue()) === '150,00', 'an amount is written the German way');
  await shot(page, 'editor-document');

  await page.click('[data-view-set="form"]');
  await page.waitForTimeout(400);
  check((await page.locator('[data-editor]').getAttribute('data-view')) === 'form', 'the switch shows the form');
  check(await page.locator('label[for="Input_RecipientStreet"]').isVisible(), 'the form shows the labels');
  await shot(page, 'editor-form');
  await page.click('[data-view-set="document"]');

  await page.getByRole('button', { name: 'Speichern', exact: true }).first().click();
  await page.waitForURL(/Invoices\/Edit\?id=\d+/, { timeout: 15000 });
  check(await page.locator('[data-line]').count() === 2, 'the draft is saved with its two positions');
  const id = new URL(page.url()).searchParams.get('id');
  // the preview stays inside the app: a dialog with the pages as images (an installed app has no way back from a PDF in a new window)
  let popup = false;
  page.once('popup', () => { popup = true; });
  // and it shows what the editor holds now: a change that is not saved is in it
  await page.locator('#Input_IntroText').fill('Vorschau ohne Speichern');
  await page.locator('#Input_IntroText').dispatchEvent('input');
  await page.locator('[data-preview-post]').first().click();
  await page.waitForFunction(() => document.querySelectorAll('#mb-preview img').length > 0 && [...document.querySelectorAll('#mb-preview img')].every(i => i.complete && i.naturalWidth > 0), null, { timeout: 15000 });
  check(await page.locator('#mb-preview').evaluate(d => d.open) && !popup, 'the PDF preview opens in a dialog of the app, not in a new window');
  const unsavedPdf = await page.request.get(base + await page.locator('[data-preview-download]').getAttribute('href'));
  check(unsavedPdf.ok() && (await unsavedPdf.body()).slice(0, 4).toString() === '%PDF', 'the PDF of the preview is the document as it stands in the editor, not saved');
  await page.waitForTimeout(400); // the dialog fades in
  await shot(page, 'pdf-preview');
  await page.locator('#mb-preview .mat-dialog__close').click();
  check(!(await page.locator('#mb-preview').evaluate(d => d.open)), 'the preview closes with its X');

  console.log('Issue it');
  await page.locator('button', { hasText: 'Ausstellen' }).first().click();
  await confirmDialog(page);
  // the form is sent after the confirmation: wait for the issued invoice, not only for a quiet network (it can be quiet before the page changes)
  await page.waitForFunction(() => /Rechnung RE-/.test((document.querySelector('h1') || {}).textContent || ''), null, { timeout: 15000 }).catch(() => {});
  const title = await page.locator('h1').innerText();
  check(/Rechnung RE-\d{4}-\d{4}/.test(title), 'the issued invoice has its number: ' + title);
  check(await page.locator('.mb-paper--view').count() === 1, 'an issued invoice is shown read only');
  const pdf = await page.request.get(base + '/invoices/' + id + '/pdf');
  check(pdf.ok() && (await pdf.body()).slice(0, 4).toString() === '%PDF', 'the PDF of the issued invoice is there');
  await shot(page, 'invoice-issued');

  console.log('Repeat it');
  await page.locator('summary', { hasText: 'Wiederholung einrichten' }).click();
  await page.locator('button', { hasText: 'Serie anlegen' }).click();
  await page.waitForURL(/Recurring\/Edit\?id=\d+/, { timeout: 15000 });
  check(await page.locator('.mb-dates li').count() > 0, 'the series shows its next dates');
  await shot(page, 'series');

  console.log('The detail search');
  await page.goto(base + '/Invoices/Edit');
  await page.click('[data-lookup-open="customers"]');
  const found = page.locator('[data-lookup-list] .mat-item__main');
  await found.first().waitFor({ timeout: 3000 });
  check(await page.locator('#mb-lookup').evaluate(d => d.open), 'the search button opens the dialog at the side');
  check(/\d+ von \d+/.test(await page.locator('[data-lookup-count]').innerText()), 'the dialog says how many it found');
  await page.fill('#mb-lookup-city', 'Heidelberg');
  await page.waitForFunction(() => document.querySelectorAll('[data-lookup-list] .mat-item__main').length === 1, null, { timeout: 3000 });
  check((await found.first().innerText()).includes('Müller'), 'the filter for the city narrows it down');
  await shot(page, 'editor-lookup');
  await found.first().click();
  check((await page.inputValue('#Input_RecipientCity')) === 'Heidelberg', 'a customer from the dialog fills the address');
  await page.locator('[data-line] [data-lookup-open="products"]').first().click();
  await found.first().waitFor({ timeout: 3000 });
  await found.first().click();
  check((await page.locator('[data-line] [data-field="UnitPrice"]').first().inputValue()) !== '', 'a product from the dialog fills the position');
  for (const w of [1440, 1280, 1024]) {
    await page.setViewportSize({ width: w, height: 900 });
    await page.waitForTimeout(250);
    const sideways = await page.evaluate(() => { const m = document.querySelector('.mat-main'); m.scrollLeft = 40; const s = m.scrollLeft; m.scrollLeft = 0; return s; });
    check(sideways === 0, 'the editor does not move sideways at ' + w + ' px');
    const paper = await page.evaluate(() => { const p = document.querySelector('.mb-paper'); return { layout: getComputedStyle(p).width, visual: Math.round(p.getBoundingClientRect().width), host: p.parentElement.clientWidth }; });
    check(paper.layout.startsWith('793') && paper.visual <= paper.host + 1, 'the paper keeps its A4 width and is made smaller at ' + w + ' px (' + paper.visual + ' of ' + paper.host + ' px)');
  }
  await page.setViewportSize({ width: 1440, height: 1000 });

  console.log('The lists');
  for (const [url, file] of [['/Invoices', 'invoices'], ['/Recurring', 'recurring'], ['/Customers', 'customers'], ['/Products', 'products'], ['/Admin/Company', 'company'], ['/Admin/Branding', 'branding'], ['/Notifications', 'notifications'], ['/Admin/Backup', 'backup'], ['/Admin/Mail', 'mail']]) {
    const response = await page.goto(base + url);
    check(response.ok() && await page.locator('h1').count() === 1, url + ' opens');
    await shot(page, file);
  }

  console.log('A quote');
  await page.goto(base + '/Quotes/Edit');
  await page.click('#Input_RecipientName');
  await page.keyboard.type('Müller');
  await page.locator('#Input_RecipientName-list .mb-combo__option').first().waitFor({ timeout: 3000 });
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await page.locator('[data-line] [data-field="Name"]').first().click();
  await page.keyboard.type('Bera');
  await page.locator('[data-line] .mb-combo__option').first().waitFor({ timeout: 3000 });
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  check((await page.locator('.mb-doc__facts').innerText()).includes('Gültig bis'), 'a quote shows how long it stands');
  await page.locator('button', { hasText: 'Ausstellen' }).first().click();
  await confirmDialog(page);
  await page.waitForLoadState('networkidle');
  await page.locator('.mb-paper--view').waitFor({ timeout: 15000 });
  check(/Angebot AN-\d{4}-\d{4}/.test(await page.locator('h1').innerText()), 'the issued quote has its number: ' + await page.locator('h1').innerText());
  await shot(page, 'quote');
  await page.locator('button', { hasText: 'In Rechnung umwandeln' }).click();
  await page.waitForURL(/Invoices\/Edit\?id=\d+/, { timeout: 15000 });
  check(await page.locator('[data-line]').count() >= 1, 'the quote became an invoice draft with its positions');

  console.log('Cancelling');
  await page.goto(base + '/Invoices/Edit?id=' + id);
  await page.locator('button', { hasText: 'Stornieren' }).click();
  const prompt = page.locator('dialog[open]').last();
  await prompt.waitFor({ state: 'visible', timeout: 5000 });
  await prompt.locator('input, textarea').first().fill('Test');
  await prompt.locator('.mat-btn--primary, .mat-btn--danger').last().click();
  await page.waitForLoadState('networkidle');
  check((await page.locator('h1').innerText()).includes('RE-'), 'the cancellation invoice opens');
  check((await page.locator('.mb-paper--view').innerText()).includes('Stornorechnung'), 'it is a cancellation invoice');
  await shot(page, 'storno');

  console.log('An expense from an e-invoice');
  const zugferd = await page.request.get(base + '/invoices/' + id + '/pdf');
  const receipt = path.join(require('os').tmpdir(), 'matbill-e2e-zugferd.pdf');
  fs.writeFileSync(receipt, await zugferd.body());
  await page.goto(base + '/Expenses');
  await page.setInputFiles('#Files', receipt);
  await page.waitForURL(/Expenses\/Edit\?id=\d+/, { timeout: 20000 });
  check((await page.inputValue('#Input_Supplier')).length > 0, 'the supplier was read from the e-invoice: ' + await page.inputValue('#Input_Supplier'));
  check((await page.locator('.mat-alert--warning:not(.mb-offline)').innerText()).includes('E-Rechnung'), 'the expense says it came from an e-invoice');
  await shot(page, 'expense');
  // the expense goes again, so that the expenses of the runs do not pile up (and deleting is tried on the way)
  const expenseUrl = page.url();
  await page.locator('.mat-btn--danger[data-confirm]').first().click();
  await confirmDialog(page);
  await page.waitForURL(/\/Expenses(\?|$)/, { timeout: 10000 });
  check((await page.request.get(expenseUrl)).status() === 404, 'the expense from the e-invoice is deleted again');

  console.log('Banking');
  // the account of an earlier run is used again, so that the accounts do not pile up with every run
  await page.goto(base + '/Banking');
  const known = await page.locator('.mb-account__name', { hasText: /^Testkonto$/ }).first().getAttribute('href').catch(() => null);
  if (known) {
    await page.goto(base + '/Banking/Import?account=' + new URL(known, base).searchParams.get('id'));
  } else {
    await page.goto(base + '/Banking/Accounts/Edit');
    await page.fill('#Input_Name', 'Testkonto');
    await page.getByRole('button', { name: 'Speichern', exact: true }).first().click();
    await page.waitForURL(/Banking\/Import/, { timeout: 15000 });
  }
  const open = await page.request.get(base + '/Invoices?status=open');
  const openHtml = await open.text();
  const number = (openHtml.match(/RE-\d{4}-\d{4}/) || [])[0];
  const csvPath = path.join(require('os').tmpdir(), 'matbill-e2e-umsaetze.csv');
  fs.writeFileSync(csvPath, 'Buchungstag;Betrag;Beguenstigter/Zahlungspflichtiger;Verwendungszweck\n' + new Date().toLocaleDateString('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric' }) + ';1,00;Jemand;Rechnung ' + number + '\n');
  await page.setInputFiles('#Statement', csvPath);
  await page.locator('button', { hasText: 'Weiter' }).click();
  await page.waitForLoadState('networkidle');
  check((await page.locator('form.mat-form').innerText()).includes('Spalten prüfen'), 'a CSV shows the columns it recognized');
  await shot(page, 'bank-import');
  await page.locator('button', { hasText: 'importieren' }).click();
  await page.waitForURL(/Banking/, { timeout: 15000 });
  await page.goto(base + '/Banking');
  check(await page.locator('#transaction-table tbody tr').count() >= 1, 'the bookings are listed');
  await shot(page, 'banking');
  await page.locator('#transaction-table tbody tr a').first().click();
  await page.waitForLoadState('networkidle');
  check(await page.locator('h1').count() === 1, 'a booking opens with its suggestions');
  await shot(page, 'bank-transaction');

  console.log('Report and export');
  await page.goto(base + '/Export');
  check(await page.locator('table.mat-table tbody tr').count() === 12, 'the report has twelve months');
  await shot(page, 'report');
  const [download] = await Promise.all([page.waitForEvent('download'), page.locator('button', { hasText: 'ZIP herunterladen' }).click()]);
  check(download.suggestedFilename().endsWith('.zip'), 'the export is a ZIP: ' + download.suggestedFilename());

  console.log('More lists');
  for (const [url, file] of [['/Quotes', 'quotes'], ['/Expenses', 'expenses']]) {
    const response = await page.goto(base + url);
    check(response.ok() && await page.locator('h1').count() === 1, url + ' opens');
    await shot(page, file);
  }

  console.log('Footer in Branding');
  await page.goto(base + '/Admin/Branding');
  const footer = () => page.locator('[data-footer-input]').evaluateAll(a => a.map(x => x.value));
  const columns = await footer();
  check(columns.length === 4 && columns.every(c => c.length > 0), 'the four columns of the footer start filled in: ' + columns.map(c => c.length).join(', '));
  check(columns[0].includes('{Name}\n') && !/&#|&amp;/.test(columns.join('')), 'the columns have real line breaks, nothing encoded twice: ' + JSON.stringify(columns[0]));
  await page.fill('[data-footer-input] >> nth=0', 'Nur eine Zeile');
  await page.locator('[data-footer-reset]').click();
  check(JSON.stringify(await footer()) === JSON.stringify(columns), '"Standard wiederherstellen" writes the standard into the columns again');
  await shot(page, 'branding-footer');

  console.log('Lists fit');
  for (const w of [1440, 1024, 820]) {
    await page.setViewportSize({ width: w, height: 900 });
    for (const url of ['/Invoices', '/Quotes', '/Recurring', '/Customers', '/Products', '/Expenses', '/Banking', '/Admin/Users']) {
      await page.goto(base + url);
      await page.waitForTimeout(200);
      const sideways = await page.evaluate(() => Math.max(0, ...[...document.querySelectorAll('.mat-table-wrap')].map(t => t.scrollWidth - t.clientWidth)));
      check(sideways === 0, url + ' does not scroll sideways at ' + w + ' px' + (sideways ? ' (' + sideways + ' px)' : ''));
    }
  }
  await page.setViewportSize({ width: 1440, height: 1000 });

  console.log('English');
  // My account › Language: the interface turns English, the documents (the paper of an invoice) stay German.
  const en = await browser.newContext({ viewport: { width: 1440, height: 1000 }, locale: 'de-DE', timezoneId: 'Europe/Berlin', storageState: await context.storageState() });
  const english = await en.newPage();
  english.on('pageerror', err => problems.push('page error (english): ' + err.message));
  await english.goto(base + '/Account/Profile');
  await english.selectOption('#Profile_Language', 'en');
  await english.locator('form[action*="handler=Profile"] button[type=submit]').first().click();
  await english.waitForLoadState('networkidle');
  check(await english.getAttribute('html', 'lang') === 'en', 'My account switches the interface to English');
  // words of the German interface that must not show up in the English one (data like customer names may be German)
  const germanWords = /\b(Speichern|Zurück|Abbrechen|Löschen|Bearbeiten|Übersicht|Einstellungen|Sortierung|Suchen|Neue Rechnung|Rechnungen|Angebote|Ausgaben|Wiederkehrend|Auswertung|Stammdaten|Kunden|Produkte|Zuordnung|Bezahlt|Überfällig|Entwurf|Fällig|Gespeichert|Abmelden|Mein Konto|Benachrichtigungen|Keine|Alle)\b/;
  for (const [url, file] of [['/', 'en-dashboard'], ['/Invoices', 'en-invoices'], ['/Invoices/Edit', 'en-editor'], ['/Invoices/Edit?id=' + id, 'en-invoice'], ['/Quotes', 'en-quotes'], ['/Recurring', 'en-recurring'],
    ['/Expenses', 'en-expenses'], ['/Banking', 'en-banking'], ['/Export', 'en-report'], ['/Customers', 'en-customers'], ['/Products', 'en-products'], ['/Admin/Company', 'en-company'], ['/Admin/Branding', 'en-branding'],
    ['/Notifications', 'en-notifications'], ['/Admin/Invoicing', 'en-settings-invoicing'], ['/Admin/Mail', 'en-settings-mail'], ['/Admin/Banking', 'en-settings-banking'], ['/Admin/Backup', 'en-backup'],
    ['/Admin/Users', 'en-users'], ['/Admin/System', 'en-system'], ['/Account/Profile', 'en-profile'], ['/Account/Security', 'en-security']]) {
    await english.goto(base + url);
    const textOfInterface = await english.evaluate(() => {
      const copy = document.body.cloneNode(true);
      // not interface: the paper, notifications (stored in the language they were written in)
      copy.querySelectorAll('.mb-paper, script, template, [hidden], .mb-preview, iframe, .mb-note').forEach(n => n.remove());
      // German data: the sample expenses, the German file names of the export for the tax office
      return copy.innerText.replace(/Fahrt zum Kunden/g, '').replace(/\b\w+\.csv\b/g, '');
    });
    const found = textOfInterface.match(germanWords);
    check(!found, url + ' has no German interface text' + (found ? ': "' + found[0] + '" in "' + textOfInterface.slice(Math.max(0, found.index - 40), found.index + 40).replace(/\s+/g, ' ') + '"' : ''));
    await shot(english, file);
  }
  await english.goto(base + '/Account/Profile');
  await english.selectOption('#Profile_Language', 'de');
  await english.locator('form[action*="handler=Profile"] button[type=submit]').first().click();
  await english.waitForLoadState('networkidle');
  check(await english.getAttribute('html', 'lang') === 'de', 'and back to German');
  await en.close();

  console.log('Dark');
  const dark = await browser.newContext({ viewport: { width: 1440, height: 1000 }, colorScheme: 'dark', locale: 'de-DE', timezoneId: 'Europe/Berlin', storageState: await context.storageState() });
  const night = await dark.newPage();
  night.on('pageerror', err => problems.push('page error (dark): ' + err.message));
  const paid = await night.goto(base + '/').then(() => night.locator('.mb-stat', { hasText: 'Bezahlt' }).innerText());
  check(!/-\s*\d/.test(paid), 'cancellation invoices do not count as payments: ' + paid.replace(/\s+/g, ' '));
  for (const [url, file] of [['/', 'dark-dashboard'], ['/Invoices/Edit', 'dark-editor']]) {
    await night.goto(base + url);
    await night.waitForTimeout(400);
    check(await night.evaluate(() => matchMedia('(prefers-color-scheme: dark)').matches), url + ' is shown dark');
    await shot(night, file);
  }
  await dark.close();

  console.log('A phone');
  const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, locale: 'de-DE', storageState: await context.storageState() });
  const mobile = await phone.newPage();
  mobile.on('pageerror', err => problems.push('page error (phone): ' + err.message));
  const lists = ['/Invoices', '/Quotes', '/Recurring', '/Customers', '/Products', '/Expenses', '/Banking', '/Admin/Users'];
  for (const [url, file] of [['/', 'mobile-dashboard'], ['/Invoices/Edit', 'mobile-editor'], ['/Invoices/Edit?id=' + id, 'mobile-invoice'], ...lists.map(l => [l, 'mobile' + l.toLowerCase().replace(/\//g, '-')])]) {
    await mobile.goto(base + url);
    const overflow = await mobile.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    check(overflow <= 1, url + ' fits the phone (overflow ' + overflow + ' px)');
    if (lists.includes(url)) {
      // a list on a phone: a row that links to its record is a card that opens it, and the first one is in view without scrolling
      const card = await mobile.evaluate(() => {
        const row = document.querySelector('.mat-table tbody tr:has(> td.mat-table__primary)');
        if (!row) { return null; }
        const r = row.getBoundingClientRect();
        const label = getComputedStyle(row.querySelector('td:not(.mat-table__primary):not(.mat-table__cell-actions)') || row, '::before');
        const dashes = [...document.querySelectorAll('.mat-table tbody tr:has(> td.mat-table__primary) > td')].filter(td => td.offsetParent && td.textContent.trim() === '–').length;
        return { top: r.top, dashes, display: getComputedStyle(row).display, label: parseFloat(label.width) || 0, actions: [...row.querySelectorAll('.mat-table__actions .mat-btn')].map(b => getComputedStyle(b, '::after').content) };
      });
      if (card) {
        check(card.display === 'flex' && card.label <= 1, url + ': a row is a record card without labels (' + card.display + ', ' + card.label + ')');
        check(card.dashes === 0, url + ': a card shows no lone dash of an empty cell (' + card.dashes + ')');
        // the accounts of Banking stand above its list, one line each: with more than three the list is lower by design
        const accounts = await mobile.locator('.mb-account').count();
        if (accounts <= 3) check(card.top < 844, url + ': the first record is in view without scrolling (top ' + Math.round(card.top) + ')');
        check(card.actions.every(c => c !== 'none' && c !== 'normal'), url + ': the actions of a card show their text: ' + card.actions.join(', '));
      }
    }
    // the app shell scrolls inside .mat-main: the shot needs a viewport as tall as the content
    const height = await mobile.evaluate(() => Math.max(844, document.querySelector('.mat-main')?.scrollHeight || 0));
    await mobile.setViewportSize({ width: 390, height: Math.min(height + 120, 6000) });
    await shot(mobile, file);
    await mobile.setViewportSize({ width: 390, height: 844 });
  }
  await mobile.goto(base + '/Invoices');
  const card = mobile.locator('.mat-table tbody tr:has(> td.mat-table__primary)').first();
  const box = await card.boundingBox();
  await mobile.mouse.click(box.x + box.width - 30, box.y + box.height / 2 - 6);
  await mobile.waitForURL(/\/Invoices\/Edit\?id=\d+/, { timeout: 5000 }).catch(() => {});
  check(/\/Invoices\/Edit\?id=\d+/.test(mobile.url()), 'a tap on a card beside the number opens the invoice: ' + mobile.url());
  const fontSize = await mobile.evaluate(() => { const el = document.querySelector('.mb-in'); return el ? parseFloat(getComputedStyle(el).fontSize) : 16; });
  check(fontSize >= 16, 'fields of the editor have 16 px on a phone (no zoom on iOS): ' + fontSize);

  console.log('Installable app');
  const manifest = await page.request.get(base + '/manifest.webmanifest');
  check(manifest.ok() && (await manifest.json()).display === 'standalone', 'the manifest is served');
  const sw = await page.request.get(base + '/sw.js');
  check(sw.ok() && (sw.headers()['cache-control'] || '').includes('no-cache'), 'the service worker is served and never cached');
  await page.goto(base + '/');
  const registered = await page.evaluate(async () => { const r = await navigator.serviceWorker.ready; return !!r.active; });
  check(registered, 'the service worker is active');

  console.log('Without a connection');
  await page.goto(base + '/Invoices');
  await context.setOffline(true);
  await page.evaluate(() => window.dispatchEvent(new Event('offline')));
  check(await page.locator('[data-offline-banner]').isVisible(), 'the page says that there is no connection');
  await page.goto(base + '/Invoices').catch(() => {});
  check((await page.locator('html').getAttribute('data-offline-copy')) !== null, 'a page that was open is shown from the kept copy');
  await page.goto(base + '/Products/Edit?id=999999').catch(() => {});
  check(await page.locator('[data-offline-page]').count() === 1 || (await page.locator('html').getAttribute('data-offline-copy')) !== null, 'a page that was never open shows the offline page');
  await shot(page, 'offline');
  await context.setOffline(false);

  await browser.close();
  if (problems.length > 0) {
    console.log('\n' + problems.length + ' problem(s):\n - ' + problems.join('\n - '));
    process.exit(1);
  }
  console.log('\nAll checks passed.');
})().catch(err => { console.error(err); process.exit(1); });
