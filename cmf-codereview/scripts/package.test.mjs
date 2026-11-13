import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createManifest, packageName, preparePackage, runtimes, validateVersion, verifyArchive, verifySmoke } from "./package.mjs";

const commit = "a".repeat(40);
const sha = "sha512-" + createHash("sha512").update("archive").digest("base64");

function fixture(t) {
  const dir = mkdtempSync(path.join(os.tmpdir(), "codereview-package-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  mkdirSync(path.join(dir, "npm")); mkdirSync(path.join(dir, "src")); mkdirSync(path.join(dir, "release"));
  writeFileSync(path.join(dir, "npm/package.json"), JSON.stringify({ name: packageName, version: "1.1.0", dependencies: { debug: "^4.4.3" } }));
  writeFileSync(path.join(dir, "src/cmf-codereview.csproj"), "<Project><Version>1.0.0-alpha</Version></Project>");
  const pack = [{ name: packageName, version: "1.1.0", filename: "candidate.tgz", integrity: sha, size: 7,
    files: runtimes.map(rid => ({ path: "dist/" + rid + "/cmf-codereview" + (rid.startsWith("win") ? ".exe" : "") })) }];
  writeFileSync(path.join(dir, "release/candidate.tgz"), "archive");
  writeFileSync(path.join(dir, "release/pack.json"), JSON.stringify(pack));
  return { dir, pack };
}

test("versions reject ranges, shell input and ambiguous normalization", () => {
  for (const value of ["1.1.0", "2.0.0-rc.1"]) assert.equal(validateVersion(value), value);
  for (const value of ["latest", "^1.0.0", "v1.1.0", "01.0.0", "1.0.0-01", "1.0.0+build", "1.0.0; echo bad", "1.0.0\n"])
    assert.throws(() => validateVersion(value), value);
});

test("npm manifest is the version source and stamping is repeatable without modifying it", t => {
  const { dir } = fixture(t);
  const pkgPath = path.join(dir, "npm/package.json");
  const original = readFileSync(pkgPath, "utf8");
  assert.equal(preparePackage(dir), "1.1.0");
  assert.equal(preparePackage(dir), "1.1.0");
  assert.equal(readFileSync(pkgPath, "utf8"), original);
  assert.match(readFileSync(path.join(dir, "src/cmf-codereview.csproj"), "utf8"), /<Version>1.1.0<\/Version>/);
});

test("invalid package identity, version or project layout stops preparation", t => {
  const { dir } = fixture(t);
  const pkgPath = path.join(dir, "npm/package.json");
  writeFileSync(pkgPath, JSON.stringify({ name: "wrong", version: "1.1.0" }));
  assert.throws(() => preparePackage(dir));
  writeFileSync(pkgPath, JSON.stringify({ name: packageName, version: "latest" }));
  assert.throws(() => preparePackage(dir));
  writeFileSync(pkgPath, JSON.stringify({ name: packageName, version: "1.1.0" }));
  writeFileSync(path.join(dir, "src/cmf-codereview.csproj"), "<Project />");
  assert.throws(() => preparePackage(dir), /Expected one project Version/);
});

test("archives retain their source commit and reject mismatched version, commit or corruption", t => {
  const { dir } = fixture(t);
  createManifest(dir, commit);
  const out = path.join(dir, "release");
  assert.equal(verifyArchive(out, "1.1.0", commit).integrity, sha);
  assert.throws(() => verifyArchive(out, "1.2.0", commit));
  assert.throws(() => verifyArchive(out, "1.1.0", "b".repeat(40)));
  writeFileSync(path.join(out, "candidate.tgz"), "corrupt");
  assert.throws(() => verifyArchive(out, "1.1.0", commit), /changed/);
});

test("missing platform bundles prevent creation of a validated manifest", t => {
  for (const rid of runtimes) {
    const { dir, pack } = fixture(t);
    pack[0].files = pack[0].files.filter(file => !file.path.startsWith("dist/" + rid + "/"));
    writeFileSync(path.join(dir, "release/pack.json"), JSON.stringify(pack));
    assert.throws(() => createManifest(dir, commit), /missing dist/);
  }
});

test("manifest rejects mismatched package metadata and integrity", t => {
  const { dir, pack } = fixture(t);
  const packPath = path.join(dir, "release/pack.json");
  pack[0].version = "1.2.0";
  writeFileSync(packPath, JSON.stringify(pack));
  assert.throws(() => createManifest(dir, commit));
  pack[0].version = "1.1.0";
  pack[0].integrity = "sha512-invalid";
  writeFileSync(packPath, JSON.stringify(pack));
  assert.throws(() => createManifest(dir, commit), /integrity mismatch/);
});

test("smoke validation requires both the expected finding and executable version", () => {
  verifySmoke([{ Rule: "LoadInsideLoop" }], "cmf-codereview 1.1.0\n", "1.1.0");
  assert.throws(() => verifySmoke([], "1.1.0", "1.1.0"), /did not scan/);
  assert.throws(() => verifySmoke([{ Rule: "LoadInsideLoop" }], "1.0.0", "1.1.0"), /unexpected version/);
});

test("command failures propagate a nonzero process exit status", t => {
  const { dir } = fixture(t);
  const helper = fileURLToPath(new URL("./package.mjs", import.meta.url));
  const result = spawnSync(process.execPath, [helper, "archive", path.join(dir, "release"), "1.1.0", commit], { encoding: "utf8" });
  assert.equal(result.status, 1);
  assert.notEqual(result.stderr.trim(), "");
});
