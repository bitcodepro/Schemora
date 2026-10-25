// Shared browser-test helpers. The app marks <html data-app-ready> once its first workspace list has loaded.
export async function openApp(page, url) {
  await page.goto(url);
  await page.waitForFunction(() => document.documentElement.dataset.appReady === 'true');
}

export async function reloadApp(page) {
  await page.reload();
  await page.waitForFunction(() => document.documentElement.dataset.appReady === 'true');
}
