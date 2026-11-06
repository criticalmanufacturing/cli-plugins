import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { createManifest, packageName, planRelease, preparePackage, requireReviewers, runtimes,
  validateBuildUrl, validateVersion, verifyArchive, verifyPromotion, verifyRegistry } from "./release.mjs";

const buildUrl = "https://dev.azure.com/example-org/Project/_build/results?buildId=12345";
const commit = "a".repeat(40);
const sha = "sha512-" + createHash("sha512").update("archive").digest("base64");

test("versions reject ranges, shell input and ambiguous normalization", () => {
  for (const value of ["1.0.1", "2.0.0-rc.1"]) assert.equal(validateVersion(value), value);
  for (const value of ["latest", "^1.0.0", "v1.0.0", "01.0.0", "1.0.0-01", "1.0.0+build", "1.0.0; echo bad", "1.0.0\n"])
    assert.throws(() => validateVersion(value), value);
});
test("disabled publishing permits a next archive build", () => {
  assert.equal(planRelease({ channel: "next", version: "1.0.1", enabled: "", ref: "refs/heads/feature" }).publish, false);
});
test("disabled publishing always blocks latest", () => {
  assert.throws(() => planRelease({ channel: "latest", version: "1.0.1", enabled: "false", ref: "refs/heads/main", buildUrl }), /blocked/);
});
test("latest needs main and an integration build URL", () => {
  assert.throws(() => planRelease({ channel: "latest", version: "1.0.1", enabled: "true", ref: "refs/heads/feature", buildUrl }), /main/);
  assert.throws(() => planRelease({ channel: "latest", version: "1.0.1", enabled: "true", ref: "refs/heads/main", buildUrl: "" }));
  assert.equal(planRelease({ channel: "latest", version: "1.0.1", enabled: "true", ref: "refs/heads/main", buildUrl }).publish, true);
});
test("test evidence links support different Azure organizations and projects", () => {
  assert.equal(validateBuildUrl(buildUrl), buildUrl);
  const otherProject = "https://dev.azure.com/other-org/Another%20Project/_build/results?buildId=67890";
  assert.equal(validateBuildUrl(otherProject), otherProject);
  for (const value of ["https://example.com/?buildId=123", buildUrl.replace('/_build/results', '/_git/repository'),
    buildUrl.replace('/Project', ''), buildUrl.replace("12345", "bad"), buildUrl.replace('https://', 'http://'),
    buildUrl.replace('https://', 'https://user:password@'), buildUrl + '#fragment'])
    assert.throws(() => validateBuildUrl(value));
});
test("promotion requires configured manual reviewers", () => {
  assert.throws(() => requireReviewers({}), /required reviewers/);
  assert.throws(() => requireReviewers({ protection_rules: [{ type: "required_reviewers", reviewers: [] }] }));
  requireReviewers({ protection_rules: [{ type: "required_reviewers", reviewers: [{ type: "User" }] }] });
});
test("promotion matches the existing next candidate and registry integrity", () => {
  verifyPromotion({ version: "1.0.1", "dist.integrity": sha }, { next: "1.0.1", latest: "1.0.0" }, "1.0.1");
  assert.throws(() => verifyPromotion({ version: "1.0.1", "dist.integrity": sha }, { next: "1.0.2" }, "1.0.1"));
  assert.throws(() => verifyPromotion({ version: "1.0.1" }, { next: "1.0.1" }, "1.0.1"));
});
test("published bytes and version match the tested package", () => {
  verifyRegistry({ version: "1.0.1", dist: { integrity: sha } }, "1.0.1", sha);
  assert.throws(() => verifyRegistry({ version: "1.0.2", "dist.integrity": sha }, "1.0.1", sha));
  assert.throws(() => verifyRegistry({ version: "1.0.1", "dist.integrity": sha }, "1.0.1", "different"));
});
test("stamping aligns npm and dotnet versions without changing dependencies", t => {
  const dir = mkdtempSync(path.join(os.tmpdir(), "codereview-release-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  mkdirSync(path.join(dir, "npm")); mkdirSync(path.join(dir, "src"));
  writeFileSync(path.join(dir, "npm/package.json"), JSON.stringify({ name: packageName, version: "1.0.0-alpha", dependencies: { debug: "^4.4.3" } }));
  writeFileSync(path.join(dir, "src/cmf-codereview.csproj"), "<Project><Version>1.0.0-alpha</Version></Project>");
  preparePackage(dir, "1.0.1");
  const pkg = JSON.parse(readFileSync(path.join(dir, "npm/package.json"), "utf8"));
  assert.equal(pkg.version, "1.0.1");
  assert.deepEqual(pkg.dependencies, { debug: "^4.4.3" });
  assert.match(readFileSync(path.join(dir, "src/cmf-codereview.csproj"), "utf8"), /<Version>1.0.1<\/Version>/);
});
test("archives require all platforms and reject corruption", t => {
  const dir = mkdtempSync(path.join(os.tmpdir(), "codereview-archive-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  mkdirSync(path.join(dir, "npm")); mkdirSync(path.join(dir, "release"));
  writeFileSync(path.join(dir, "npm/package.json"), JSON.stringify({ name: packageName, version: "1.0.1" }));
  const pack = [{ name: packageName, version: "1.0.1", filename: "candidate.tgz", integrity: sha, size: 7,
    files: runtimes.map(rid => ({ path: "dist/" + rid + "/cmf-codereview" + (rid.startsWith("win") ? ".exe" : "") })) }];
  writeFileSync(path.join(dir, "release/candidate.tgz"), "archive");
  const packFile = path.join(dir, "release/pack.json");
  writeFileSync(packFile, JSON.stringify(pack));
  createManifest(dir, commit);
  verifyArchive(path.join(dir, "release"), "1.0.1", commit);
  assert.throws(() => verifyArchive(path.join(dir, "release"), "1.0.1", "b".repeat(40)));
  pack[0].files.pop(); writeFileSync(packFile, JSON.stringify(pack));
  assert.throws(() => createManifest(dir, commit), /missing dist\/osx-x64/);
  writeFileSync(path.join(dir, "release/candidate.tgz"), "corrupt");
  assert.throws(() => verifyArchive(path.join(dir, "release"), "1.0.1", commit), /changed/);
});
