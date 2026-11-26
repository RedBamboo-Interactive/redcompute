// Live acceptance. Run only with the coordinated test and deploy slot.
// 1. Run rebuild.ps1 -PrepareOnly from the canonical RedCompute checkout.
// 2. node scripts/verify-inference-settings.cjs prepare <requestPath> <statePath>
// 3. node scripts/verify-inference-settings.cjs arm <statePath>
// 4. From a session with a fresh signed bearer, run the verify phase with the same statePath.
// LEAF_PLAYWRIGHT_MODULE can point to the workspace's Playwright installation.
// LEAF_BROWSER_STORAGE_STATE is an already authenticated human Playwright state in scratch.
// Set LEAF_VERIFY_HARNESS=1 to require the provider switch and return in this same run.

const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const path = require("node:path");
const { setTimeout: delay } = require("node:timers/promises");

const leaf = "http://127.0.0.1:18804";
const compute = "http://127.0.0.1:18800";
const bearer = process.env.REDLEAF_EXECUTION_TOKEN;
const [phase, ...args] = process.argv.slice(2);
const timeout = 180_000;

async function api(origin, route, { method = "GET", body, key, status = [200] } = {}) {
  assert.ok(origin === leaf || origin === compute);
  const headers = { Authorization: "Bearer " + bearer };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (key) headers["X-Idempotency-Key"] = key;
  const response = await fetch(origin + route, {
    method, headers, body: body === undefined ? undefined : JSON.stringify(body),
    redirect: "error", signal: AbortSignal.timeout(30_000),
  });
  const json = await response.json();
  assert.ok(status.includes(response.status), method + " " + route + " returned "
    + response.status + (json.error ? " (" + json.error + ")" : ""));
  return json;
}

async function readJson(file) {
  return JSON.parse((await fs.readFile(file, "utf8")).replace(/^\uFEFF/, ""));
}

async function eventually(label, read) {
  const until = Date.now() + timeout;
  do {
    const result = await read();
    if (result) return result;
    await delay(750);
  } while (Date.now() < until);
  throw new Error("Timed out waiting for " + label);
}

const sessionRoute = ({ sessionId }) => "/ai-session/sessions/" + encodeURIComponent(sessionId);
const settingRoute = fixture => sessionRoute(fixture) + "/inference-settings";
const settings = fixture => api(compute, settingRoute(fixture));
const selection = ({ model, effort }) => ({ model, effort });
const snapshot = fixture => api(compute, sessionRoute(fixture) + "?tail=100");
const readItem = (fixture, id) => api(compute, sessionRoute(fixture) + "/input-queue/" + encodeURIComponent(id));
const terminal = (fixture, id) => eventually("receipt " + id, async () => {
  const item = await readItem(fixture, id);
  assert.ok(!["failed", "cancelled"].includes(item.state), id + " " + (item.error?.code ?? item.state));
  return item.state === "delivered" ? item : null;
});
const idle = fixture => eventually("idle " + fixture.sessionId, async () => {
  const result = await snapshot(fixture);
  return result.session.status.toLowerCase() === "idle" && result.session.providerSessionId ? result : null;
});

function effortChoice(options) {
  const allowed = options.models.find(model => model.id === options.model)?.supportedEfforts ?? [];
  const effort = ["low", "medium", "high", ...allowed].find(value => value !== options.effort && allowed.includes(value));
  assert.ok(effort, "Fixture must offer two Thinking choices");
  return effort;
}

function modelChoice(options, preferred) {
  const target = options.models.find(model => model.id !== options.model && model.id === preferred)
    ?? options.models.find(model => model.id !== options.model && model.supportedEfforts.includes(options.effort))
    ?? options.models.find(model => model.id !== options.model);
  assert.ok(target, "Fixture must offer two Model choices");
  return target.id;
}

async function apply(fixture, change, step, wait = true) {
  const route = settingRoute(fixture);
  const key = fixture.key + "-" + step;
  const preview = await api(compute, route + "?validateOnly=true", { method: "PUT", body: change });
  assert.equal(preview.operation ?? null, null);
  assert.ok(preview.proposed);
  for (const [name, value] of Object.entries(change)) assert.equal(preview.proposed[name], value);
  const accepted = await api(compute, route, { method: "PUT", body: change, key, status: [200, 202] });
  const id = accepted.operation?.id;
  assert.ok(id, "Expected an idempotent operation receipt");
  const replay = await api(compute, route, { method: "PUT", body: change, key, status: [200, 202] });
  assert.equal(replay.operation?.id, id);
  if (!wait) return { id, change };
  const done = await terminal(fixture, id);
  assert.equal(done.kind, "inference-settings");
  assert.equal(done.turnId ?? null, null);
  assert.equal(done.deliveredMessageUid ?? null, null);
  for (const [name, value] of Object.entries(change)) assert.equal(done.metadata.applied[name], value);
  assert.deepEqual(selection(await settings(fixture)), done.metadata.applied);
  return done.metadata.applied;
}

async function message(fixture, step, target) {
  const marker = "inference-verification-" + fixture.key + "-" + step;
  const body = {
    content: "Reply with only " + marker + ". Use no tools and take no other action.",
    delivery: "after-current",
    ...(target ? { deploymentVerificationTarget: target } : {}),
  };
  const accepted = await api(compute, sessionRoute(fixture) + "/message",
    { method: "POST", body, key: marker, status: [200, 202] });
  assert.ok(accepted.queueItemId && accepted.messageUid);
  return { id: accepted.queueItemId, uid: accepted.messageUid, marker };
}

function assertTranscript(messages, sent) {
  const positions = sent.map(entry => {
    const matches = messages.map((message, index) => ({ message, index }))
      .filter(({ message }) => message.role === "user" && message.messageUid === entry.uid);
    assert.equal(matches.length, 1, "Expected exactly one user record for " + entry.marker);
    assert.ok(matches[0].message.content.includes(entry.marker));
    if (entry.provider) assert.equal(matches[0].message.provider, entry.provider);
    const nextUser = messages.findIndex((message, index) => index > matches[0].index && message.role === "user");
    const reply = messages.slice(matches[0].index + 1, nextUser < 0 ? undefined : nextUser)
      .some(record => record.role === "assistant" && record.content?.includes(entry.marker)
        && (!entry.provider || record.provider === entry.provider));
    assert.ok(reply, "Missing reply for " + entry.marker);
    return matches[0].index;
  });
  assert.deepEqual(positions, [...positions].sort((a, b) => a - b));
}

async function verifyTurn(fixture, step, expected) {
  assert.deepEqual(selection(await settings(fixture)), expected);
  const sent = await message(fixture, step);
  const receipt = await terminal(fixture, sent.id);
  assert.equal(receipt.deliveredMessageUid, sent.uid);
  await idle(fixture);
  await eventually("shared transcript for " + step, async () => {
    const result = await snapshot(fixture);
    assert.equal(result.session.providerSessionId, fixture.nativeId);
    try { assertTranscript(result.messages, [sent]); return true; }
    catch (error) { if (!(error instanceof assert.AssertionError)) throw error; return false; }
  });
  assert.deepEqual(selection(await settings(fixture)), expected);
  fixture.sent.push(sent);
  console.log(JSON.stringify({ provider: fixture.provider, step, nativeId: fixture.nativeId, effective: expected }));
}

async function createFixture(provider, runId) {
  const key = runId + "-" + provider;
  if (provider === "codex") {
    const repositories = (await api(leaf, "/api/apps/codered/repositories")).items;
    const repository = repositories.find(item => item.slug?.startsWith("redcompute-") && item.path)
      ?? repositories.find(item => item.path);
    assert.ok(repository?.id, "An existing registered repository is required for the Code fixture");
    const catalogue = (await api(compute, "/ai-session/models?provider=" + provider)).models;
    const model = catalogue[0]?.id;
    assert.ok(model, "The installed Codex catalogue must offer a model for the Code fixture");
    const created = await api(compute, "/ai-session/sessions", {
      method: "POST",
      body: { provider, model, repositoryId: repository.id, projectPath: repository.path },
    });
    assert.ok(created.id);
    const fixture = { provider, key, sessionId: created.id, sent: [] };
    const ready = await message(fixture, "ready");
    await terminal(fixture, ready.id);
    const { session } = await idle(fixture);
    assert.equal(session.provider, provider);
    fixture.nativeId = session.providerSessionId;
    return fixture;
  }
  const created = await api(leaf, "/api/apps/nova/discussions", {
    method: "POST",
    body: {
      provider, title: "Acceptance: AI Session settings " + provider + " " + runId,
      idempotencyKey: key, liveTimeline: false,
      brief: "This is a disposable settings acceptance discussion. Reply only READY. Use no tools.",
    },
  });
  assert.ok(created.id);
  const fixture = { provider, key, discussionId: created.id, sent: [] };
  const detail = await eventually("Nova session creation for " + provider, async () => {
    const result = await api(leaf, "/api/apps/nova/discussions/" + encodeURIComponent(created.id) + "?tail=1");
    return result.discussion?.sessionId ? result.discussion : null;
  });
  fixture.sessionId = detail.sessionId;
  const { session } = await idle(fixture);
  assert.equal(session.provider, provider);
  fixture.nativeId = session.providerSessionId;
  return fixture;
}

async function modalChanges(context, fixture, initial, desiredModel) {
  const page = await context.newPage();
  try {
    const url = fixture.provider === "codex"
      ? "/apps/codered/sessions/" + encodeURIComponent(fixture.sessionId)
      : "/apps/nova/chat/" + encodeURIComponent(fixture.discussionId);
    await page.goto(leaf + url, { waitUntil: "domcontentloaded" });
    await page.locator('[data-slot="panel-header"]').getByRole("button", { name: "Conversation actions", exact: true }).click({ timeout: 30_000 });
    await page.getByRole("menuitem", { name: /^Info\b/ }).click();
    const dialog = page.getByRole("dialog").filter({ has: page.locator('[data-slot="session-info-hero"]') });
    await dialog.getByRole("tab", { name: "Settings", exact: true }).click();
    const model = dialog.getByRole("combobox", { name: "Model" });
    const thinking = dialog.getByRole("combobox", { name: "Thinking" });
    assert.equal(await model.inputValue(), initial.model);
    const effort = effortChoice(await settings(fixture));
    await thinking.selectOption(effort);
    const newEffort = await eventually("modal Thinking confirmation", async () => {
      const options = await settings(fixture);
      return options.effort === effort ? selection(options) : null;
    });
    await verifyTurn(fixture, "modal-thinking", newEffort);
    const targetModel = modelChoice(await settings(fixture), desiredModel);
    await model.selectOption(targetModel);
    const newModel = await eventually("modal Model confirmation", async () => {
      const options = await settings(fixture);
      return options.model === targetModel ? selection(options) : null;
    });
    await verifyTurn(fixture, "modal-model", newModel);
  } finally {
    await page.close();
  }
}

async function stageBoundary(fixture, target, harness) {
  const current = await settings(fixture);
  const first = await message(fixture, "before-boundary", target);
  const change = harness ? { provider: "codex" } : { effort: effortChoice(current) };
  const operation = await apply(fixture, change, "restart-boundary", false);
  const last = await message(fixture, "after-boundary");
  const entries = await Promise.all([first, operation, last].map(entry => readItem(fixture, entry.id)));
  assert.ok(entries.every(entry => entry.state === "pending"), "Expected all three items to wait for the exact restart");
  assert.ok(entries[0].sequence < entries[1].sequence && entries[1].sequence < entries[2].sequence);
  fixture.boundary = { previous: selection(current), first, operation, last, originalProvider: harness ? current.provider : null };
}

async function discover() {
  const summary = await api(compute, "/discover");
  assert.match(summary.areas.find(area => area.id === "ai-session").summary, /Thinking/i);
  const search = await api(compute, "/discover/find?q=inference-settings");
  for (const method of ["GET", "PUT"]) assert.ok(search.routes.some(route =>
    route.method === method && route.path === "/ai-session/sessions/{id}/inference-settings"));
  const openapi = await api(compute, "/openapi.json");
  const put = openapi.paths["/ai-session/sessions/{id}/inference-settings"].put;
  assert.match(put.summary, /validateOnly/);
  assert.match(put.summary, /metadata\.applied/);
  assert.match(put.summary, /409 idempotency_conflict/);
}

async function prepare(requestPath, statePath) {
  await assert.rejects(fs.access(statePath), { code: "ENOENT" });
  const request = await readJson(requestPath);
  assert.equal(path.basename(requestPath), "redcompute-rebuild-" + request.runId + ".request.json");
  await fs.access(request.manifestPath);
  await assert.rejects(fs.access(request.receiptPath), { code: "ENOENT" });
  const storageState = process.env.LEAF_BROWSER_STORAGE_STATE;
  assert.ok(storageState, "LEAF_BROWSER_STORAGE_STATE is required for the real modal checks");
  await fs.access(storageState);
  await discover();
  const { chromium } = require(process.env.LEAF_PLAYWRIGHT_MODULE || "playwright");
  const browserEnvironment = { ...process.env };
  delete browserEnvironment.REDLEAF_EXECUTION_TOKEN;
  const browser = await chromium.launch({ headless: true, env: browserEnvironment });
  const providers = ["claude-code", "codex"];
  const partialPath = statePath + ".partial";
  let fixtures = [];
  try {
    try {
      const partial = await readJson(partialPath);
      assert.equal(partial.runId, request.runId);
      assert.equal(partial.requestPath, requestPath);
      assert.ok(partial.fixtures.length <= providers.length);
      assert.deepEqual(partial.fixtures.map(fixture => fixture.provider), providers.slice(0, partial.fixtures.length));
      fixtures = partial.fixtures;
    } catch (error) {
      if (error.code !== "ENOENT") throw error;
    }
    const context = await browser.newContext({ storageState, viewport: { width: 1440, height: 1000 } });
    for (const provider of providers.slice(fixtures.length)) {
      const attempt = process.env.LEAF_VERIFY_ATTEMPT;
      assert.ok(!attempt || /^[a-zA-Z0-9-]{1,20}$/.test(attempt), "Invalid LEAF_VERIFY_ATTEMPT label");
      const fixture = await createFixture(provider, attempt ? request.runId + "-" + attempt : request.runId);
      const before = await settings(fixture);
      const harness = process.env.LEAF_VERIFY_HARNESS === "1" && provider === "claude-code";
      if (harness) {
        assert.equal(before.provider, "claude-code");
        assert.ok(before.providers?.some(option => option.id === "codex"), "Server did not offer a Codex harness switch");
      }
      const thinking = await apply(fixture, { effort: effortChoice(before) }, "api-thinking");
      await verifyTurn(fixture, "api-thinking", thinking);
      const model = await apply(fixture, { model: modelChoice(await settings(fixture)) }, "api-model");
      await verifyTurn(fixture, "api-model", model);
      await modalChanges(context, fixture, model, before.model);
      await stageBoundary(fixture, { service: "redcompute", runId: request.runId }, harness);
      fixtures.push(fixture);
      await fs.writeFile(partialPath + ".new", JSON.stringify({ runId: request.runId, requestPath, fixtures }));
      await fs.rename(partialPath + ".new", partialPath);
    }
  } finally {
    await browser.close();
  }
  const state = { requestPath, receiptPath: request.receiptPath, runId: request.runId, fixtures };
  await fs.writeFile(statePath, JSON.stringify(state, null, 2), { flag: "wx" });
  console.log(JSON.stringify({ statePath, runId: state.runId, fixtures: fixtures.map(({ provider, discussionId, sessionId }) =>
    ({ provider, discussionId, sessionId })) }));
}

async function verifyHarnessReturn(fixture, change) {
  const { first, last, originalProvider } = fixture.boundary;
  const prepared = change.metadata.applied.native;
  assert.equal(prepared.target.providerId, "codex");
  assert.notEqual(prepared.nativeThreadId, fixture.nativeId);
  const active = await idle(fixture);
  assert.equal(active.session.provider, "codex");
  assert.equal(active.session.providerSessionId, prepared.nativeThreadId);
  const chronological = [...fixture.sent, { ...first, provider: originalProvider }, { ...last, provider: "codex" }];

  await eventually("mixed history after the first switch", async () => {
    try { assertTranscript((await snapshot(fixture)).messages, chronological); return true; }
    catch (error) { if (!(error instanceof assert.AssertionError)) throw error; return false; }
  });

  const storageState = process.env.LEAF_BROWSER_STORAGE_STATE;
  assert.ok(storageState, "LEAF_BROWSER_STORAGE_STATE is required for the modal provider return");
  const { chromium } = require(process.env.LEAF_PLAYWRIGHT_MODULE || "playwright");
  const browserEnvironment = { ...process.env };
  delete browserEnvironment.REDLEAF_EXECUTION_TOKEN;
  const browser = await chromium.launch({ headless: true, env: browserEnvironment });
  try {
    const context = await browser.newContext({ storageState, viewport: { width: 1440, height: 1000 } });
    const page = await context.newPage();
    await page.goto(leaf + "/apps/nova/chat/" + encodeURIComponent(fixture.discussionId), { waitUntil: "domcontentloaded" });
    await page.locator('[data-slot="session-info-trigger"]').click({ timeout: 30_000 });
    const dialog = page.getByRole("dialog").filter({ has: page.locator('[data-slot="session-info-hero"]') });
    await dialog.getByRole("tab", { name: "Settings", exact: true }).click();
    const provider = dialog.getByRole("combobox", { name: "Provider" });
    assert.equal(await provider.inputValue(), "codex");
    await provider.selectOption(originalProvider);
    await eventually("modal provider return", async () => (await settings(fixture)).provider === originalProvider);
  } finally {
    await browser.close();
  }
  const returned = await idle(fixture);
  assert.equal(returned.session.provider, originalProvider);
  assert.equal(returned.session.providerSessionId, fixture.nativeId);
  const sent = await message(fixture, "after-harness-return");
  await terminal(fixture, sent.id);
  await idle(fixture);
  await eventually("Claude to Codex to Claude history", async () => {
    try { assertTranscript((await snapshot(fixture)).messages, [...chronological, { ...sent, provider: originalProvider }]); return true; }
    catch (error) { if (!(error instanceof assert.AssertionError)) throw error; return false; }
  });
  console.log(JSON.stringify({ discussionId: fixture.discussionId, sessionId: fixture.sessionId,
    providers: [originalProvider, "codex", originalProvider], originalNativeId: fixture.nativeId, codexNativeId: prepared.nativeThreadId }));
}

async function verify(state) {
  const deployment = await readJson(state.receiptPath);
  assert.equal(deployment.runId, state.runId);
  assert.equal(deployment.state, "succeeded");
  assert.equal(deployment.success, true);
  await discover();
  for (const fixture of state.fixtures) {
    const { first, last, operation, previous } = fixture.boundary;
    const [earlier, change, later] = await Promise.all([first, operation, last].map(entry => terminal(fixture, entry.id)));
    assert.ok(earlier.sequence < change.sequence && change.sequence < later.sequence);
    assert.ok(Date.parse(earlier.completedAt) <= Date.parse(change.completedAt));
    assert.ok(Date.parse(change.completedAt) <= Date.parse(later.completedAt));
    assert.equal(earlier.deliveredMessageUid, first.uid);
    assert.equal(later.deliveredMessageUid, last.uid);
    assert.equal(change.turnId ?? null, null);
    assert.equal(change.deliveredMessageUid ?? null, null);
    if (fixture.boundary.originalProvider) {
      assert.equal(change.kind, "execution-binding");
      await verifyHarnessReturn(fixture, change);
      continue;
    }
    assert.deepEqual(change.metadata.applied, { ...previous, ...operation.change });
    await idle(fixture);
    await eventually("complete ordered transcript after restart", async () => {
      const result = await snapshot(fixture);
      assert.equal(result.session.providerSessionId, fixture.nativeId);
      try { assertTranscript(result.messages, [...fixture.sent, first, last]); return true; }
      catch (error) { if (!(error instanceof assert.AssertionError)) throw error; return false; }
    });
    assert.deepEqual(selection(await settings(fixture)), change.metadata.applied);
    console.log(JSON.stringify({ provider: fixture.provider, discussionId: fixture.discussionId,
      sessionId: fixture.sessionId, nativeId: fixture.nativeId, before: previous, after: change.metadata.applied,
      incomingOrder: [earlier.id, change.id, later.id] }));
  }
}

async function main() {
  assert.ok(bearer, "REDLEAF_EXECUTION_TOKEN is required");
  const identity = await api(leaf, "/auth/execution-context");
  assert.equal(identity.tokenUse, "execution");
  assert.equal(identity.identity?.actor?.kind, "agent", "Use a signed Agent execution identity");
  assert.ok(process.env.REDLEAF_SCRATCH_DIR, "REDLEAF_SCRATCH_DIR is required");
  assert.ok(["prepare", "arm", "verify"].includes(phase), "Choose prepare, arm, or verify");
  assert.equal(args.length, phase === "prepare" ? 2 : 1, "Expected request and state paths for prepare, or state path alone");
  const statePath = path.resolve(phase === "prepare" ? args[1] : args[0]);
  const relative = path.relative(path.resolve(process.env.REDLEAF_SCRATCH_DIR), statePath);
  assert.ok(relative && !relative.startsWith("..") && !path.isAbsolute(relative), "State path must be in session scratch");
  if (phase === "prepare") return prepare(path.resolve(args[0]), statePath);
  const state = await readJson(statePath);
  assert.deepEqual(state.fixtures.map(fixture => fixture.provider), ["claude-code", "codex"]);
  if (phase === "verify") return verify(state);
  assert.equal(phase, "arm");
  const request = await readJson(state.requestPath);
  assert.equal(request.runId, state.runId);
  await assert.rejects(fs.access(state.receiptPath), { code: "ENOENT" });
  const admission = await api(compute, "/maintenance/deploy-staged",
    { method: "POST", body: { requestPath: state.requestPath }, status: [202] });
  assert.equal(admission.accepted, true);
  console.log(JSON.stringify({ runId: state.runId, deployment: admission.state }));
}

main().catch(error => { console.error(error.stack); process.exitCode = 1; });
