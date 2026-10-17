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
const email = process.env.MB_EMAIL || 'admin@example.test';
const password = process.env.MB_PASSWORD || 'Admin-Passwort-2026';
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
    check(page.url().includes('/Branding'), 'the setup ends on the branding page');
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
  check(await page.locator('.mb-stat').count() === 4, 'four number cards on the overview');
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

  await page.locator('button', { hasText: 'Speichern' }).first().click();
  await page.waitForURL(/Invoices\/Edit\?id=\d+/, { timeout: 15000 });
  check(await page.locator('[data-line]').count() === 2, 'the draft is saved with its two positions');
  const id = new URL(page.url()).searchParams.get('id');

  console.log('Issue it');
  await page.locator('button', { hasText: 'Ausstellen' }).first().click();
  await confirmDialog(page);
  await page.waitForLoadState('networkidle');
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

  console.log('The lists');
  for (const [url, file] of [['/Invoices', 'invoices'], ['/Recurring', 'recurring'], ['/Customers', 'customers'], ['/Products', 'products'], ['/Branding', 'branding'], ['/Notifications', 'notifications'], ['/Admin/Backup', 'backup'], ['/Admin/Mail', 'mail']]) {
    const response = await page.goto(base + url);
    check(response.ok() && await page.locator('h1').count() === 1, url + ' opens');
    await shot(page, file);
  }

  console.log('A phone');
  const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, locale: 'de-DE', storageState: await context.storageState() });
  const mobile = await phone.newPage();
  mobile.on('pageerror', err => problems.push('page error (phone): ' + err.message));
  for (const [url, file] of [['/', 'mobile-dashboard'], ['/Invoices/Edit', 'mobile-editor'], ['/Invoices', 'mobile-invoices'], ['/Invoices/Edit?id=' + id, 'mobile-invoice']]) {
    await mobile.goto(base + url);
    const overflow = await mobile.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    check(overflow <= 1, url + ' fits the phone (overflow ' + overflow + ' px)');
    await shot(mobile, file);
  }
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
