const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createFlow, challengeRequest, install3dsHandling } = require('../wwwroot/wallet-flow.js');

const deferred = () => {
  const result = { resolved: [], rejected: [] };
  result.resolve = value => result.resolved.push(value);
  result.reject = error => result.rejected.push(error);
  return result;
};
const tick = () => new Promise(resolve => setImmediate(resolve));
const order = { orderId: 'wallet-test', token: 'server-created-token' };

test('click starts order immediately, opens wallet synchronously, and blocks charge until backend resolves', async () => {
  let finish, calls = 0;
  const flow = createFlow({ amount: () => 100, createOrder: () => {
    calls++; return new Promise(resolve => { finish = resolve; });
  } });
  const before = deferred(), after = deferred();
  flow.before(before);
  assert.equal(calls, 1);
  assert.equal(before.resolved.length, 1);
  flow.after(after, { amount: 100 });
  await tick();
  assert.equal(after.resolved.length, 0);
  finish(order);
  await tick();
  assert.deepEqual(after.resolved, [{ token: order.token }]);
});

for (const [name, createOrder] of [
  ['backend failure', () => Promise.reject(new Error('503'))],
  ['synchronous backend failure', () => { throw new Error('failure'); }],
  ['missing token', () => ({ orderId: 'wallet-test' })],
  ['missing order ID', () => ({ token: 'unlinked-token' })],
]) test(name + ' never releases the payment gate', async () => {
  const flow = createFlow({ amount: () => 100, createOrder });
  const after = deferred();
  flow.before(deferred());
  await tick();
  flow.after(after, { amount: 100 });
  await tick();
  assert.equal(after.resolved.length, 0);
  assert.equal(after.rejected.length, 1);
});

test('second click cannot create an overlapping order', () => {
  let calls = 0;
  const flow = createFlow({ amount: () => 100, createOrder: () => { calls++; return order; } });
  flow.before(deferred());
  const second = deferred();
  flow.before(second);
  assert.equal(calls, 1);
  assert.equal(second.rejected.length, 1);
});

test('cancellation while waiting for backend blocks a late token', async () => {
  let finish;
  const flow = createFlow({ amount: () => 100, createOrder: () => new Promise(r => { finish = r; }) });
  const after = deferred();
  flow.before(deferred()); flow.after(after, { amount: 100 }); flow.cancel();
  finish(order); await tick();
  assert.equal(after.resolved.length, 0);
  assert.equal(after.rejected.length, 1);
});

test('changed amount does not submit payment', async () => {
  const flow = createFlow({ amount: () => 100, createOrder: () => order });
  const after = deferred();
  flow.before(deferred()); flow.after(after, { amount: 200 }); await tick();
  assert.equal(after.resolved.length, 0);
  assert.equal(after.rejected.length, 1);
});

test('invalid amount does not call backend or open wallet', () => {
  const flow = createFlow({ amount: () => null, createOrder: () => assert.fail() });
  const before = deferred(); flow.before(before);
  assert.equal(before.resolved.length, 0);
  assert.equal(before.rejected.length, 1);
});

const model = challenge => ({ attr: key => key === 'submit3ds' ? challenge : undefined });
test('3DS POST preserves the original Flitt return URL and challenge fields', () => {
  const fields = { PaReq: 'opaque', TermUrl: 'https://pay.flitt.com/return', MD: 'opaque' };
  assert.deepEqual(challengeRequest(model({ url: 'https://bank.example/3ds', send_data: fields })), {
    url: 'https://bank.example/3ds', method: 'POST', fields,
  });
});
test('3DS GET redirect and unsafe destination rejection', () => {
  assert.equal(challengeRequest(model({ url: 'https://bank.example/3ds' })).method, 'GET');
  assert.throws(() => challengeRequest(model({ url: 'javascript:alert(1)' })));
});
test('API adapter handles challenge progress once; it does not force 3DS on normal payments', () => {
  let progress, navigations = 0;
  const pending = { progress: cb => { progress = cb; return pending; } };
  const api = install3dsHandling({ request: () => pending }, { navigate: () => navigations++ });
  assert.equal(api.request('api.checkout.form', 'request', {}), pending);
  assert.equal(navigations, 0);
  progress(model({ url: 'https://bank.example/3ds' }));
  progress(model({ url: 'https://bank.example/3ds' }));
  assert.equal(navigations, 1);
});
