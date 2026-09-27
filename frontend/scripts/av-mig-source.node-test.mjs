import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { inputs } from './check-av-mig-source.mjs';
import { resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { tmpdir } from 'node:os';

const values = { failure: 1, loginIndex: 1, ownerIndex: 0, success: 0 };
{
  const cases = [
      ['baseline', () => inputs(), values.success],
      [
        'missing state owner',
        (inventory) => {
          inventory.routes[values.loginIndex].stateOwners = [];
        },
        values.failure,
      ],
      [
        'unknown owner',
        (inventory) => {
          inventory.routes[values.loginIndex].stateOwners.push('InventedFacade');
        },
        values.failure,
      ],
      [
        'synchronized route deletion',
        (...data) => {
          data.forEach((document) => document.routes.pop());
        },
        values.failure,
      ],
      [
        'synchronized duplicate',
        (...data) => {
          data.forEach((document) => document.routes.push(document.routes[values.loginIndex]));
        },
        values.failure,
      ],
      [
        'freeze disagreement',
        (inventory, freeze) => {
          freeze.routes[values.loginIndex].november = false;
        },
        values.failure,
      ],
      [
        'orphan owner',
        (inventory) => {
          inventory.stateOwners[values.ownerIndex].sourcePath = 'missing.ts';
        },
        values.failure,
      ],
      [
        'synchronized owner removal',
        (inventory, freeze) => {
          inventory.routes[values.loginIndex].stateOwners = ['LoginPageComponent'];
          freeze.routes[values.loginIndex].stateOwner = 'LoginPageComponent';
        },
        values.failure,
      ],
    ],
    runProbe = (directory, mutate) => {
      const data = inputs(),
        path = resolve(directory, 'inputs.json'),
        script = fileURLToPath(new URL('./check-av-mig-source.mjs', import.meta.url));
      mutate(...data);
      writeFileSync(path, JSON.stringify(data));
      return spawnSync(process.execPath, [script, path], { encoding: 'utf8' });
    };
  for (const [name, mutate, expected] of cases) {
    test(`production source inventory CLI: ${name}`, () => {
      const directory = mkdtempSync(resolve(tmpdir(), 'av-mig-'));
      try {
        const result = runProbe(directory, mutate);
        assert.equal(result.status, expected, result.stderr);
      } finally {
        rmSync(directory, { force: true, recursive: true });
      }
    });
  }
}

{
  const routeSourcePath = fileURLToPath(new URL('../src/app/app.routes.ts', import.meta.url)),
    script = fileURLToPath(new URL('./check-av-mig-source.mjs', import.meta.url)),
    cases = [
      [
        'component binding drift',
        (source) => source.replace('m.LoginPageComponent', 'm.SessionExpiredPageComponent'),
      ],
      [
        'redirect target drift',
        (source) => source.replace("redirectTo: 'login'", "redirectTo: 'workspaces'"),
      ],
      [
        'fallback kind drift',
        (source) => source.replace('component: PagePlaceholderComponent,', "redirectTo: 'login',"),
      ],
    ];

  for (const [name, mutate] of cases) {
    test(`production source inventory CLI rejects ${name}`, () => {
      const directory = mkdtempSync(resolve(tmpdir(), 'av-mig-route-'));
      try {
        const dataPath = resolve(directory, 'inputs.json'),
          routePath = resolve(directory, 'app.routes.ts'),
          original = readFileSync(routeSourcePath, 'utf8'),
          mutated = mutate(original);
        assert.notEqual(mutated, original, `${name}: mutation must alter route source`);
        writeFileSync(dataPath, JSON.stringify(inputs()));
        writeFileSync(routePath, mutated);
        const result = spawnSync(process.execPath, [script, dataPath, routePath], { encoding: 'utf8' });
        assert.equal(result.status, values.failure, result.stderr);
      } finally {
        rmSync(directory, { force: true, recursive: true });
      }
    });
  }
}
