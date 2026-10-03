const argumentCount = 2,
  firstArgumentIndex = 0,
  gates = process.argv.slice(argumentCount);
if (!gates.length) {
  gates.push('functional-fast', 'functional-full');
}
if (gates.some((gate) => !['functional-fast', 'functional-full'].includes(gate))) {
  throw new Error('Usage: node tests/ui/run-real-backend-fci04.mjs [functional-fast] [functional-full]');
}
process.env.COGLATAS_FCI04_GATES = [...new Set(gates)].join(',');
process.env.COGLATAS_FCI04_ONLY = '1';
// Gate arguments belong to the host runner, not Playwright path filters.
process.argv = process.argv.slice(firstArgumentIndex, argumentCount);
await import('./run-real-backend-smoke-compose.mjs');
