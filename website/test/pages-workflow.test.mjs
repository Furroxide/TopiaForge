import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { parse } from 'yaml';

const workflowUrl = new URL('../../.github/workflows/deploy-pages.yml', import.meta.url);
const retiredWorkflowUrl = new URL(
  '../../.github/workflows/deploy-launcher-updates.yml',
  import.meta.url,
);
const workflow = parse(readFileSync(fileURLToPath(workflowUrl), 'utf8'));

test('complete Pages workflow has trusted CI, manual, and stable-release entrypoints', () => {
  assert.deepEqual(workflow.on.workflow_run.workflows, ['CI']);
  assert.deepEqual(workflow.on.workflow_run.types, ['completed']);
  assert.deepEqual(workflow.on.release.types, ['published']);
  assert.deepEqual(workflow.on.workflow_dispatch, null);
  assert.equal(existsSync(fileURLToPath(retiredWorkflowUrl)), false);

  const build = workflow.jobs['build-pages'];
  assert.match(build.if, /conclusion == 'success'/u);
  assert.match(build.if, /event == 'push'/u);
  assert.match(build.if, /head_branch == 'main'/u);
  assert.match(build.if, /startsWith\(github\.ref, 'refs\/tags\/v'\)/u);
  const checkout = build.steps.find((step) => step.name === 'Checkout exact triggering revision');
  assert.match(checkout.with.ref, /workflow_run\.head_sha/u);
  assert.match(checkout.uses, /^actions\/checkout@9c091bb/u);
  const trustedSource = build.steps.find(
    (step) => step.name === 'Require a trusted Pages source',
  );
  assert.match(trustedSource.run, /refs\/heads\/main/u);
  assert.match(trustedSource.run, /releases\/tags\/\$SOURCE_TAG/u);
  assert.match(trustedSource.run, /\.immutable == true/u);
  assert.equal(build.permissions.contents, 'read');
  assert.equal(build.permissions.pages, 'read');
});

test('stable releases dispatch their exact immutable tags without checkout', () => {
  const dispatch = workflow.jobs['dispatch-stable-release'];
  assert.deepEqual(dispatch.permissions, { actions: 'write', contents: 'read' });
  assert.match(dispatch.if, /!github\.event\.release\.prerelease/u);
  assert.equal(dispatch.steps.some((step) => 'uses' in step), false);
  assert.match(dispatch.steps[0].run, /releases\/\$RELEASE_ID/u);
  assert.match(dispatch.steps[0].run, /\.immutable == true/u);
  assert.match(
    dispatch.steps[1].run,
    /gh workflow run deploy-pages\.yml --ref "\$RELEASE_TAG"/u,
  );
});

test('protected deployment executes no repository commands and smoke has no privileges', () => {
  const deploy = workflow.jobs.deploy;
  assert.deepEqual(deploy.permissions, { pages: 'write', 'id-token': 'write' });
  assert.equal(deploy.steps.length, 1);
  assert.match(deploy.steps[0].uses, /^actions\/deploy-pages@/u);
  assert.equal('run' in deploy.steps[0], false);

  const smoke = workflow.jobs.smoke;
  assert.deepEqual(smoke.permissions, {});
  assert.deepEqual(smoke.needs, ['build-pages', 'deploy']);
  assert.match(smoke.steps[0].run, /https:\/\/docs\.topiaforge\.dev/u);
  assert.match(smoke.steps[0].run, /manual-releases\.json/u);
});

test('publication lock is global and non-cancelling', () => {
  assert.equal(workflow.concurrency.group, 'topiaforge-pages-publication');
  assert.equal(workflow.concurrency['cancel-in-progress'], false);
});
