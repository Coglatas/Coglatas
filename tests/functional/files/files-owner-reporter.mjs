/** Reject a selected Files owner that never executed successfully. */
export default class FilesOwnerReporter {
  onBegin(_config, suite) {
    this.listOnly = process.argv.includes('--list');
    this.selected = suite.allTests();
  }

  onEnd(result) {
    if (this.listOnly || result.status !== 'passed') {
      return;
    }
    const selected = this.selected ?? [];
    const owners = selected.filter((entry) => entry.annotations.some((annotation) =>
      annotation.type === 'journey' && annotation.description === 'FUNC-FILE-002',
    ));
    if (selected.length === 0 || owners.some((entry) =>
      entry.expectedStatus !== 'passed' || entry.results.length === 0 ||
      entry.results.some((attempt) => attempt.status !== 'passed'),
    )) {
      console.error('FCI-05 owner evidence is empty, skipped, or unsuccessful; refusing Green.');
      return { status: 'failed' };
    }
  }
}
