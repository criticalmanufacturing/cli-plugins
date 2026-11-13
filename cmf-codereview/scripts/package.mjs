import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { appendFileSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

export const packageName = "@criticalmanufacturing/codereview";
export const runtimes = ["win-x64", "linux-x64", "osx-x64"];

export function validateVersion(version) {
  assert.equal(typeof version, "string", "An exact package version is required");
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/.exec(version);
  assert.ok(match && version.length <= 100, "Use an exact SemVer without a v prefix or build metadata");
  for (const identifier of (match[4] || "").split(".")) {
    assert.ok(!/^\d+$/.test(identifier) || identifier === "0" || !identifier.startsWith("0"),
      "Numeric prerelease identifiers cannot have leading zeroes");
  }
  return version;
}

function json(file) { return JSON.parse(readFileSync(file, "utf8").replace(/^\uFEFF/, "")); }
function integrity(file) { return "sha512-" + createHash("sha512").update(readFileSync(file)).digest("base64"); }

export function preparePackage(directory) {
  const manifest = json(path.join(directory, "npm/package.json"));
  assert.equal(manifest.name, packageName);
  const version = validateVersion(manifest.version);
  const projectPath = path.join(directory, "src/cmf-codereview.csproj");
  const project = readFileSync(projectPath, "utf8");
  assert.equal((project.match(/<Version>[^<]*<\/Version>/g) || []).length, 1, "Expected one project Version");
  writeFileSync(projectPath, project.replace(/<Version>[^<]*<\/Version>/, "<Version>" + version + "</Version>"));
  return version;
}

export function createManifest(directory, commit) {
  assert.match(commit, /^[0-9a-f]{40}$/, "A source commit is required");
  const pack = json(path.join(directory, "release/pack.json"));
  assert.equal(pack.length, 1, "Expected one npm archive");
  const entry = pack[0];
  const manifest = json(path.join(directory, "npm/package.json"));
  assert.equal(entry.name, packageName);
  assert.equal(entry.version, manifest.version);
  validateVersion(entry.version);
  assert.equal(path.basename(entry.filename), entry.filename, "Invalid archive filename");
  assert.ok(entry.filename.endsWith(".tgz"));
  for (const rid of runtimes) {
    const executable = "dist/" + rid + "/cmf-codereview" + (rid.startsWith("win") ? ".exe" : "");
    assert.ok(entry.files?.some(file => file.path === executable), "Archive is missing " + executable);
  }
  const archive = path.join(directory, "release", entry.filename);
  assert.equal(entry.integrity, integrity(archive), "Archive integrity mismatch");
  const result = {
    schemaVersion: 1, name: packageName, version: entry.version, commit,
    file: entry.filename, integrity: entry.integrity, size: entry.size, runtimes
  };
  writeFileSync(path.join(directory, "release/manifest.json"), JSON.stringify(result, null, 2) + "\n");
  return result;
}

export function verifyArchive(directory, version, commit) {
  const manifest = json(path.join(directory, "manifest.json"));
  assert.equal(manifest.schemaVersion, 1);
  assert.equal(manifest.name, packageName);
  assert.equal(manifest.version, validateVersion(version));
  if (commit) assert.equal(manifest.commit, commit);
  assert.equal(path.basename(manifest.file), manifest.file);
  assert.ok(manifest.file.endsWith(".tgz"));
  assert.equal(integrity(path.join(directory, manifest.file)), manifest.integrity, "Archive was changed");
  assert.deepEqual(manifest.runtimes, runtimes);
  return manifest;
}

export function verifySmoke(findings, executableVersion, expectedVersion) {
  assert.ok(Array.isArray(findings) && findings.some(finding => finding.Rule === "LoadInsideLoop"),
    "The installed package did not scan the fixture correctly");
  validateVersion(expectedVersion);
  assert.ok(executableVersion.trim().split(/\s+/).includes(expectedVersion),
    "Installed executable reports an unexpected version: " + executableVersion.trim());
}

function output(name, value) {
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, name + "=" + value + "\n");
}
function summary(text) {
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, text + "\n");
  console.log(text);
}

function main(command, ...args) {
  switch (command) {
    case "prepare": {
      const version = preparePackage(process.cwd());
      output("version", version);
      summary("Package version from npm/package.json: " + version);
      break;
    }
    case "manifest": {
      const manifest = createManifest(process.cwd(), process.env.GITHUB_SHA);
      summary("Built " + manifest.file + " (" + manifest.size + " bytes), including Windows, Linux and Intel macOS x64.\nIntegrity: " + manifest.integrity);
      break;
    }
    case "archive": console.log(verifyArchive(args[0], args[1], args[2]).file); break;
    case "smoke": verifySmoke(json(args[0]), readFileSync(args[1], "utf8"), args[2]); break;
    default: throw new Error("Unknown package command: " + command);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try { main(...process.argv.slice(2)); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
