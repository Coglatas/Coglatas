import { dirname, resolve } from 'node:path';
import { readFileSync, readdirSync } from 'node:fs';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

export class SourceInventory {
  static root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
  static parse(path) {
    return ts.createSourceFile(path, readFileSync(path, 'utf8'), ts.ScriptTarget.Latest, true);
  }
  static read(path) {
    return JSON.parse(readFileSync(resolve(SourceInventory.root, path), 'utf8'));
  }
  static unique(values, label) {
    assert.equal(new Set(values).size, values.length, `${label}: duplicates`);
    return [...values].sort();
  }
  static property(node, name) {
    return node.properties.find((item) => item.name?.getText() === name)?.initializer;
  }
  static componentName(node) {
    if (!node) {
      return null;
    }
    if (ts.isIdentifier(node)) {
      return node.text;
    }
    const candidates = [];
    const visit = (current) => {
      if (ts.isPropertyAccessExpression(current) && current.name.text.endsWith('Component')) {
        candidates.push(current.name.text);
      }
      ts.forEachChild(current, visit);
    };
    visit(node);
    assert.equal(candidates.length, 1, 'Unsupported dynamic route component binding');
    return candidates[0];
  }
  static routeRecords(array, prefix = '') {
    assert.ok(array && ts.isArrayLiteralExpression(array), 'Unsupported dynamic route array');
    return array.elements.flatMap((entry) => {
      assert.ok(ts.isObjectLiteralExpression(entry), 'Unsupported route expression');
      const children = SourceInventory.property(entry, 'children'),
        fragment = SourceInventory.property(entry, 'path');
      assert.ok(fragment && ts.isStringLiteral(fragment), 'Unsupported dynamic route path');
      const joined = [prefix, fragment.text].filter(Boolean).join('/');
      if (children) {
        return SourceInventory.routeRecords(children, joined);
      }

      const path = joined === '**' ? '**' : `/${joined}`,
        redirect = SourceInventory.property(entry, 'redirectTo'),
        component = SourceInventory.property(entry, 'component'),
        loadComponent = SourceInventory.property(entry, 'loadComponent');

      if (redirect) {
        assert.ok(ts.isStringLiteral(redirect), `${path}: unsupported dynamic redirect`);
        return [{
          path,
          kind: 'redirect',
          redirectTo: redirect.text.startsWith('/') ? redirect.text : `/${redirect.text}`,
        }];
      }

      const componentName = SourceInventory.componentName(component ?? loadComponent);
      if (path === '**') {
        assert.ok(componentName, '**: fallback must declare a component');
        return [{ path, kind: 'fallback', component: componentName }];
      }

      assert.ok(componentName, `${path}: screen must declare component or loadComponent`);
      return [{ path, kind: 'screen', component: componentName }];
    });
  }
  static routeRecordsFrom(source) {
    const declaration = source.statements
      .filter(ts.isVariableStatement)
      .flatMap((statement) => [...statement.declarationList.declarations])
      .find((item) => item.name.getText() === 'routes');
    return SourceInventory.routeRecords(declaration?.initializer);
  }
  static routesFrom(source) {
    return SourceInventory.routeRecordsFrom(source).map((route) => route.path);
  }
  static classSources() {
    const sources = new Map();
    for (const file of readdirSync(resolve(SourceInventory.root, 'frontend/src/app'), {
      recursive: true,
    }).filter(
      (name) => name.endsWith('.ts') && !name.endsWith('.spec.ts') && !name.endsWith('.stories.ts'),
    )) {
      const path = resolve(SourceInventory.root, 'frontend/src/app', file);
      for (const node of SourceInventory.parse(path).statements.filter(ts.isClassDeclaration)) {
        if (node.name) {
          sources.set(node.name.text, path);
        }
      }
    }
    return sources;
  }
  static namedImports(source) {
    const imports = new Map();
    for (const item of source.statements
      .filter(ts.isImportDeclaration)
      .filter((entry) => entry.moduleSpecifier.text.startsWith('.'))) {
      const bindings = item.importClause?.namedBindings;
      if (bindings && ts.isNamedImports(bindings)) {
        for (const binding of bindings.elements) {
          imports.set(binding.name.text, {
            name: binding.propertyName?.text ?? binding.name.text,
            path: resolve(dirname(source.fileName), `${item.moduleSpecifier.text}.ts`),
          });
        }
      }
    }
    return imports;
  }
  static injectedOwners(source, owners, sources) {
    const declared = new Set(),
      imports = SourceInventory.namedImports(source),
      visit = (node) => {
        if (ts.isCallExpression(node) && node.expression.getText() === 'inject') {
          const [argument] = node.arguments,
            dependency = imports.get(argument?.getText());
          if (dependency && owners.has(dependency.name)) {
            assert.equal(
              sources.get(dependency.name),
              dependency.path,
              'Injected owner import mismatch',
            );
            declared.add(dependency.name);
          }
        }
        ts.forEachChild(node, visit);
      };
    visit(source);
    return [...declared];
  }
  static verifyScreen(route, row, context) {
    const { owners, sources } = context,
      declared = new Set([
        route.component,
        ...SourceInventory.injectedOwners(
          SourceInventory.parse(resolve(SourceInventory.root, row.ownerPath)),
          owners,
          sources,
        ),
      ]);
    assert.equal(
      sources.get(route.component),
      resolve(SourceInventory.root, row.ownerPath),
      `${route.path}: component path drift`,
    );
    assert.ok(
      route.stateOwners.includes(route.component),
      `${route.path}: missing component owner`,
    );
    assert.deepEqual(
      [...route.stateOwners].sort(),
      [...declared].sort(),
      `${route.path}: directly injected state owner drift`,
    );
  }
  static verifyRow(route, row, context) {
    assert.ok(route.stateOwners?.length, `${route.path}: missing state owner`);
    SourceInventory.unique(route.stateOwners, `${route.path} owners`);
    assert.equal(
      row.stateOwner,
      route.stateOwners.join(' + '),
      `${route.path}: owner disagreement`,
    );
    for (const name of route.stateOwners) {
      assert.ok(context.owners.has(name), `${route.path}: unknown owner ${name}`);
    }
    if (route.kind === 'screen') {
      SourceInventory.verifyScreen(route, row, context);
    }
    assert.equal(
      route.demoScope === 'november-core',
      row.november,
      `${route.path}: November scope disagreement`,
    );
    assert.equal(
      row.freeze === 'November Required',
      row.november,
      `${route.path}: freeze class disagreement`,
    );
  }
  static verify(data, routeSource) {
    const [inventory, freeze, target] = data,
      freezeByPath = new Map(freeze.routes.map((route) => [route.path, route])),
      owners = new Map(inventory.stateOwners.map((owner) => [owner.name, owner])),
      declaredRoutes = SourceInventory.routeRecordsFrom(
        routeSource ??
          SourceInventory.parse(resolve(SourceInventory.root, freeze.routeDefinition)),
      ),
      declaredByPath = new Map(declaredRoutes.map((route) => [route.path, route])),
      paths = SourceInventory.unique(
        declaredRoutes.map((route) => route.path),
        'production routes',
      ),
      sources = SourceInventory.classSources();
    for (const [label, document] of [
      ['inventory', inventory],
      ['freeze', freeze],
      ['target', target],
    ]) {
      assert.deepEqual(
        SourceInventory.unique(
          document.routes.map((route) => route.path),
          label,
        ),
        paths,
        `${label}: production route drift`,
      );
    }
    SourceInventory.unique(
      inventory.stateOwners.map((owner) => owner.name),
      'owner registry',
    );
    for (const owner of owners.values()) {
      if (owner.name !== 'Router') {
        assert.equal(
          sources.get(owner.name),
          resolve(SourceInventory.root, owner.sourcePath),
          `${owner.name}: orphan source owner`,
        );
      }
    }
    for (const route of inventory.routes) {
      SourceInventory.verifyRow(route, freezeByPath.get(route.path), { owners, sources });
      const declared = declaredByPath.get(route.path);
      assert.ok(declared, `${route.path}: missing production route declaration`);
      assert.equal(declared.kind, route.kind, `${route.path}: route kind drift`);
      if (route.kind === 'redirect') {
        assert.equal(declared.redirectTo, route.redirectTo, `${route.path}: redirect target drift`);
      } else {
        assert.equal(declared.component, route.component, `${route.path}: route component binding drift`);
      }
    }
    return paths.length;
  }
  static loadInputs(path) {
    if (path) { return JSON.parse(readFileSync(path, 'utf8')); }
    return SourceInventory.inputs();
  }
  static inputs() {
    return [
      SourceInventory.read('docs/migration/avalonia/angular-frontend-inventory.json'),
      SourceInventory.read('docs/migration/avalonia/angular-feature-freeze-matrix.json'),
      SourceInventory.read('docs/migration/avalonia/angular-target-surface-map-v5.8.1.json'),
    ];
  }
}
export const inputs = () => SourceInventory.inputs(),
  verify = (data, routeSource) => SourceInventory.verify(data, routeSource);
{
  const [, script, dataPath, routePath] = process.argv;
  if (script && resolve(script) === fileURLToPath(import.meta.url)) {
    try {
      const data = SourceInventory.loadInputs(dataPath),
        routeSource = routePath && SourceInventory.parse(resolve(routePath));
      process.stdout.write(
        `AV-MIG source inventory verified: ${verify(data, routeSource)} routes\n`,
      );
    } catch (error) {
      process.stderr.write(`${error.message}\n`);
      process.exitCode = 1;
    }
  }
}
