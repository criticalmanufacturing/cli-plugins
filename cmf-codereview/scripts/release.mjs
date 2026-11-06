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

export function validateBuildUrl(value) {
  const url = new URL(value);
  assert.equal(url.origin, "https://dev.azure.com", "Use an Azure DevOps integration build URL");
  assert.match(url.pathname, /^\/[^/]+\/[^/]+\/_build\/results\/?$/i, "Use an organization/project build results URL");
  assert.match(url.searchParams.get("buildId") || "", /^[1-9]\d*$/, "An integration build ID is required");
  assert.ok(!url.username && !url.password && !url.hash, "Invalid build URL");
  return url.toString();
}

export function planRelease({ channel, version, enabled, buildUrl, ref }) {
  assert.ok(["next", "latest"].includes(channel), "Channel must be next or latest");
  validateVersion(version);
  if (channel === "latest") {
    assert.equal(enabled, "true", "Publishing is blocked until the RepoFlow upload limit is resolved");
    assert.equal(ref, "refs/heads/main", "Promote latest only from the main workflow");
    validateBuildUrl(buildUrl);
  }
  return { channel, version, publish: enabled === "true" };
}

export function requireReviewers(environment) {
  assert.ok(environment.protection_rules?.some(rule =>
    rule.type === "required_reviewers" && rule.reviewers?.length > 0),
  "Configure required reviewers on the repoflow-latest environment before enabling promotion");
}

function json(file) { return JSON.parse(readFileSync(file, "utf8").replace(/^\uFEFF/, "")); }
function integrity(file) { return "sha512-" + createHash("sha512").update(readFileSync(file)).digest("base64"); }

export function preparePackage(directory, version) {
  validateVersion(version);
  const manifestPath = path.join(directory, "npm/package.json");
  const manifest = json(manifestPath);
  assert.equal(manifest.name, packageName);
  const projectPath = path.join(directory, "src/cmf-codereview.csproj");
  const project = readFileSync(projectPath, "utf8");
  assert.equal((project.match(/<Version>[^<]*<\/Version>/g) || []).length, 1, "Expected one project Version");
  manifest.version = version;
  writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n");
  writeFileSync(projectPath, project.replace(/<Version>[^<]*<\/Version>/, "<Version>" + version + "</Version>"));
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

export function verifyRegistry(metadata, version, expectedIntegrity) {
  assert.equal(metadata.version, validateVersion(version), "Registry version mismatch");
  const actual = metadata.dist?.integrity || metadata["dist.integrity"];
  assert.match(actual || "", /^sha512-[A-Za-z0-9+/]+={0,2}$/, "Registry SHA-512 integrity is missing");
  if (expectedIntegrity) assert.equal(actual, expectedIntegrity, "Published package differs from tested archive");
  return actual;
}

export function verifyPromotion(metadata, tags, version) {
  verifyRegistry(metadata, version);
  assert.equal(tags.next, version, "Only the currently published next candidate can be promoted");
}

function output(name, value) {
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, name + "=" + value + "\n");
}
function summary(text) {
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, text + "\n");
  console.log(text);
}

async function main(command, ...args) {
  const env = process.env;
  switch (command) {
    case "plan": {
      const plan = planRelease({ channel: env.RELEASE_CHANNEL, version: env.RELEASE_VERSION,
        enabled: env.CODE_REVIEW_PUBLISH_ENABLED, buildUrl: env.INTEGRATION_BUILD_URL, ref: env.GITHUB_REF });
      output("version", plan.version);
      output("publish", String(plan.publish));
      summary("Version: " + plan.version + "; channel: " + plan.channel +
        (plan.publish ? "" : "\nPublishing is DISABLED. This run only builds and tests the archive."));
      if (plan.channel === "latest") summary("Integration run to review before approval: " + validateBuildUrl(env.INTEGRATION_BUILD_URL));
      break;
    }
    case "check-approval": {
      const response = await fetch("https://api.github.com/repos/criticalmanufacturing/cli-plugins/environments/repoflow-latest", {
        headers: { authorization: "Bearer " + env.GITHUB_TOKEN, accept: "application/vnd.github+json",
          "X-GitHub-Api-Version": "2022-11-28" }, signal: AbortSignal.timeout(20000)
      });
      assert.ok(response.ok, "Cannot inspect repoflow-latest required reviewers: HTTP " + response.status);
      requireReviewers(await response.json());
      break;
    }
    case "prepare": preparePackage(process.cwd(), args[0]); break;
    case "manifest": {
      const manifest = createManifest(process.cwd(), env.GITHUB_SHA);
      summary("Built " + manifest.file + " (" + manifest.size + " bytes), including Windows, Linux and macOS x64.\nIntegrity: " + manifest.integrity);
      break;
    }
    case "archive": {
      const manifest = verifyArchive(args[0], args[1], args[2]);
      output("archive", manifest.file);
      console.log(manifest.file);
      break;
    }
    case "unpublished":
      assert.equal(json(args[0]).error?.code, "E404", "Registry lookup failed; only a confirmed E404 allows publication");
      break;
    case "registry": verifyRegistry(json(args[0]), args[1], args[2]); break;
    case "promotion":
      verifyPromotion(json(args[0]), json(args[1]), args[2]);
      break;
    case "smoke": {
      const findings = json(args[0]);
      assert.ok(Array.isArray(findings) && findings.some(finding => finding.Rule === "LoadInsideLoop"),
        "The installed package did not scan the fixture correctly");
      const version = readFileSync(args[1], "utf8").trim();
      assert.ok(version.split(/\s+/).includes(args[2]), "Installed executable reports an unexpected version: " + version);
      break;
    }
    default: throw new Error("Unknown release command: " + command);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(...process.argv.slice(2)).catch(error => { console.error(error.message); process.exitCode = 1; });
}
