const path = require('path');
const { test, expect } = require('@playwright/test');

test('factorial page computes correct results', async ({ page }) => {
  const filePath = path.join(__dirname, '../factorial.html');
  await page.goto(`file://${filePath}`);

  await page.fill('#i', '0');
  await expect(page.locator('#t')).toHaveValue('1');

  await page.fill('#i', '5');
  await expect(page.locator('#t')).toHaveValue('120');

  await page.fill('#i', '-1');
  await expect(page.locator('#t')).toHaveValue('Error');

  await page.fill('#i', '4.5');
  await expect(page.locator('#t')).toHaveValue('Error');
});
