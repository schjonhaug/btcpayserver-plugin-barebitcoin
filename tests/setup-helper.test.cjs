const { test } = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const { requestAccounts } = require('../barebitcoin-lightning-connection-setup');

function fakeRequest(run) {
  return (_, onResponse) => {
    const req = new EventEmitter();
    req.destroy = error => { req.emit('error', error); req.emit('close'); };
    req.end = () => {
      const res = new EventEmitter();
      res.setEncoding = () => {};
      res.statusCode = 200;
      onResponse(res);
      run(res, req);
    };
    return req;
  };
}

test('a stalled response has a deadline', async () => {
  await assert.rejects(requestAccounts({}, fakeRequest(() => {}), 5), /timed out/);
});
test('an aborted response rejects instead of hanging', async () => {
  await assert.rejects(requestAccounts({}, fakeRequest((res, req) => {
    res.emit('aborted'); req.emit('close');
  })), /interrupted/);
});
test('successful account response resolves', async () => {
  const result = await requestAccounts({}, fakeRequest((res, req) => {
    res.emit('data', '{"accounts":[]}'); res.emit('end'); req.emit('close');
  }));
  assert.deepEqual(result, {statusCode: 200, body: '{"accounts":[]}'});
});
