import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const angularJson = JSON.parse(await readFile(new URL('../angular.json', import.meta.url), 'utf8')),
  coglatasPackageJson = JSON.parse(await readFile(new URL('../../coglatas-frontend/package.json', import.meta.url), 'utf8')),
  coglatasPackageLock = JSON.parse(await readFile(new URL('../../coglatas-frontend/package-lock.json', import.meta.url), 'utf8')),
  expectedDependencies = {
    '@lucide/angular': '1.48.0',
    '@microsoft/signalr': '10.0.11',
    '@syncfusion/ej2-angular-gantt': '35.1.37',
    '@syncfusion/ej2-angular-grids': '34.2.9',
    '@syncfusion/ej2-angular-inputs': '34.2.8',
    '@syncfusion/ej2-angular-popups': '34.2.8',
    'ag-grid-angular': '36.2.0',
    'ag-grid-community': '36.2.0',
    rxjs: '7.8.2',
    'zone.js': '0.16.3',
  },
  expectedDevDependencies = {
    '@angular-devkit/build-angular': '22.2.0',
    '@storybook/angular': '10.6.0',
    jsdom: '30.1.1',
    storybook: '10.6.0',
    vitest: '5.0.2',
  },
  packageJson = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8'));

test('keeps the reviewed Angular 22 third-party versions pinned', () => {
  for (const [name, version] of Object.entries(expectedDependencies)) {
    assert.equal(packageJson.dependencies[name], version, `${name} drifted from the ANG22-06 reviewed version`);
  }
  for (const [name, version] of Object.entries(expectedDevDependencies)) {
    assert.equal(packageJson.devDependencies[name], version, `${name} drifted from the ANG22-06 reviewed version`);
  }
});

test('keeps the sibling Storybook documentation workspace on Compodoc 2.x', () => {
  assert.equal(coglatasPackageJson.devDependencies['@compodoc/compodoc'], '^2.0.0');
  assert.equal(coglatasPackageLock.packages['node_modules/@compodoc/compodoc'].version, '2.0.0');
});

test('retains the Angular Storybook browser builder and zone.js runtime', () => {
  const { architect } = angularJson.projects.frontend;
  const browserTarget = architect['storybook-browser'];
  const storybookTarget = architect.storybook;
  const buildStorybookTarget = architect['build-storybook'];

  assert.equal(browserTarget.builder, '@angular-devkit/build-angular:browser');
  assert.deepEqual(browserTarget.options.polyfills, ['zone.js']);
  assert.equal(storybookTarget.options.browserTarget, 'frontend:storybook-browser');
  assert.equal(buildStorybookTarget.options.browserTarget, 'frontend:storybook-browser');
  assert.equal(storybookTarget.options.compodoc, false);
  assert.equal(buildStorybookTarget.options.compodoc, false);
});

test('retains Syncfusion license and theme sanitation gates', () => {
  assert.match(packageJson.scripts['syncfusion:activate'], /require-syncfusion-license\.mjs/u);
  assert.match(packageJson.scripts['build-storybook'], /sanitize-syncfusion-theme-css\.mjs/u);

  const { assets } = angularJson.projects.frontend.architect.build.options;
  const assetInputs = assets.map((asset) => typeof asset === 'string' ? asset : asset.input);
  for (const requiredInput of [
    'node_modules/@syncfusion/ej2-base/styles',
    'node_modules/@syncfusion/ej2-grids/styles',
    'node_modules/@syncfusion/ej2-popups/styles',
    'node_modules/@syncfusion/ej2-gantt/styles',
  ]) {
    assert.equal(assetInputs.includes(requiredInput), true, `${requiredInput} is missing from the production asset contract`);
  }
});

test('keeps the Angular 22 Vitest runner contract on the reviewed jsdom toolchain', () => {
  const testTarget = angularJson.projects.frontend.architect.test;

  assert.equal(testTarget.builder, '@angular/build:unit-test');
  assert.equal(testTarget.options.runnerConfig, 'vitest.config.ts');
});
