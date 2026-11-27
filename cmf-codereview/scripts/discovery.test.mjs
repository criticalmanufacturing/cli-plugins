import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const tools = resolve(dirname(fileURLToPath(import.meta.url)), '../tools');
const shell = process.platform === 'win32' ? 'powershell.exe' : 'pwsh';
function run(name, cwd, args = []) {
  const result = spawnSync(shell, ['-NoProfile', '-File', join(tools, name), ...args], { cwd, encoding: 'utf8' });
  assert.equal(result.error, undefined);
  assert.equal(result.status, 0, result.stderr);
}
const json = path => JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, ''));

test('discovery pipeline produces inspectable chains and retains noisy candidates', () => {
  const root = mkdtempSync(join(tmpdir(), 'catalog-discovery-'));
  try {
    mkdirSync(join(root, 'Orchestration'));
    writeFileSync(join(root, 'Orchestration', 'Example.cs'), `public class Operations {
public void Outer() {
  Mid();
}
public void Mid() {
  output.Material.TrackOut();
  output.Material.Load();
  output.Material.Select();
}
}`);
    run('discover-nested-action-groups.ps1', root, ['-RepoRoot', root, '-OrchestrationSubPath', 'Orchestration']);
    run('join-chains.ps1', root);
    run('dedupe-chains.ps1', root);
    const candidates = json(join(root, 'candidates.json'));
    assert.ok(candidates.some(x => x.Outer === 'Outer' && x.Target === 'Mid'));
    assert.ok(candidates.some(x => x.Target === 'Material.Load'));
    assert.ok(!candidates.some(x => x.Target === 'Material.Select'));
    assert.equal(json(join(root, 'joined-chains.json')).length, 2);
    assert.equal(json(join(root, 'unique-pairs.json')).length, 1);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('empty input remains an array through all three tools', () => {
  const root = mkdtempSync(join(tmpdir(), 'catalog-empty-'));
  try {
    mkdirSync(join(root, 'Orchestration'));
    run('discover-nested-action-groups.ps1', root, ['-RepoRoot', root, '-OrchestrationSubPath', 'Orchestration']);
    run('join-chains.ps1', root);
    run('dedupe-chains.ps1', root);
    for (const file of ['candidates.json', 'joined-chains.json', 'unique-pairs.json']) assert.deepEqual(json(join(root, file)), []);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
