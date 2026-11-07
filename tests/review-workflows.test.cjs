const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;

function script(name) {
  const file = name === 'codex' ? 'codex-pull-request-code-review.yml' : 'gemini-pull-request-code-review.yml';
  const source = fs.readFileSync(`${__dirname}/../.github/workflows/${file}`, 'utf8');
  const step = source.slice(source.indexOf(`- name: Post ${name === 'codex' ? 'Codex' : 'Gemini'} feedback`));
  const block = step.split('script: |\n')[1].split('\n');
  const lines = [];
  for (const line of block) {
    if (line.trim() && !line.startsWith('            ')) break;
    lines.push(line.slice(12));
  }
  return new AsyncFunction('github', 'context', 'process', lines.join('\n'));
}
for (const name of ['gemini']) {
  test(`${name}: stale head cannot post feedback`, async () => {
    let posted = false;
    await script(name)({ rest: { pulls: {get: async () => ({data: {head: {sha:'new'}}})}, issues: {createComment: async () => {posted=true;}} }},
      {repo:{owner:'o',repo:'r'},payload:{pull_request:{head:{sha:'old'},number:1}}},
      {env:{CODEX_FINAL_MESSAGE:'review', GEMINI_RESPONSE:'review'}});
    assert.equal(posted,false);
  });
  test(`${name}: paginated bot-owned matching-head comment is updated`, async () => {
    let updated;
    let pagination;
    const issues = {listComments: {}, updateComment: async value => {updated=value;}, createComment: async () => assert.fail('must update'), deleteComment: async () => {}};
    const marker=`<!-- ${name}-review:head -->`;
    await script(name)({rest:{pulls:{get:async () => ({data:{head:{sha:'head'}}})},issues},paginate:async (method,args) => {
      pagination=args;
      return [{id:1,user:{login:'someone'},body:marker},{id:2,user:{login:'github-actions[bot]'},body:marker}];
    }},{repo:{owner:'o',repo:'r'},payload:{pull_request:{head:{sha:'head'},number:1}}},
    {env:{CODEX_FINAL_MESSAGE:'review',GEMINI_RESPONSE:'review'}});
    assert.equal(pagination.per_page,100);
    assert.equal(updated.comment_id,2);
    assert.match(updated.body,/Commit: head/);
  });
}
