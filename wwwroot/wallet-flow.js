(function (root) {
  'use strict';

  // One page = one attempt. Reload explicitly for a new attempt, including after an
  // ambiguous network result. Never automatically retry a wallet charge.
  function createFlow({ createOrder, amount, onState = () => {}, onOrder = () => {} }) {
    let attempt = null;
    return {
      before(defer) {
        if (attempt) { defer.reject(new Error('An attempt is already active.')); return; }
        const value = amount();
        if (!Number.isSafeInteger(value) || value < 1 || value > 100000000) {
          defer.reject(new Error('Enter an amount between 0.01 and 1,000,000 GEL.'));
          return;
        }
        const current = attempt = { amount: value, cancelled: false };
        onState('opening');
        // Start now, but resolve before synchronously to preserve Apple's click gesture.
        // Convert rejection to a result immediately so cancellation cannot leave an
        // unhandled rejection while the wallet is still open.
        try {
          current.order = Promise.resolve(createOrder(value)).then(
            order => ({ order }), error => ({ error }));
        } catch (error) { current.order = Promise.resolve({ error }); }
        defer.resolve();
      },
      after(defer, params) {
        const current = attempt;
        if (!current || current.cancelled) { defer.reject(new Error('No active attempt.')); return; }
        onState('initializing');
        current.order.then(({ order, error }) => {
          if (error) throw error;
          if (current.cancelled) throw new Error('Wallet attempt cancelled.');
          if (Number(params.amount) !== current.amount) throw new Error('Amount changed; start again.');
          if (!order || typeof order.orderId !== 'string' || !order.orderId
              || typeof order.token !== 'string' || !order.token.trim())
            throw new Error('Backend did not return an order and token. Payment blocked.');
          onOrder(order);
          onState('processing');
          // This is the only point that permits the SDK to submit wallet data.
          defer.resolve({ token: order.token });
        }).catch(error => {
          onState('blocked', error);
          defer.reject(error);
        });
      },
      cancel() { if (attempt) attempt.cancelled = true; },
    };
  }

  function challengeRequest(model) {
    const challenge = model.attr('submit3ds');
    if (!challenge || typeof challenge.url !== 'string') throw new Error('Missing 3DS destination.');
    const url = new URL(challenge.url);
    if (url.protocol !== 'https:' || url.username || url.password) throw new Error('Invalid 3DS destination.');
    const fields = challenge.send_data || {};
    if (typeof fields !== 'object' || Array.isArray(fields)
        || Object.values(fields).some(value => value !== null && !['string', 'number', 'boolean'].includes(typeof value)))
      throw new Error('Invalid 3DS form data.');
    const method = String(challenge.method || (challenge.send_data ? 'POST' : 'GET')).toUpperCase();
    if (!['GET', 'POST'].includes(method)) throw new Error('Unsupported 3DS method.');
    // Keep Flitt's TermUrl and challenge fields intact. Do NOT use prepare3dsData():
    // that method rewrites TermUrl for an iframe, whereas we navigate the whole page.
    return { url: url.href, method, fields };
  }

  function navigateChallenge({ url, method, fields }) {
    if (method === 'GET' && Object.keys(fields).length === 0) { location.assign(url); return; }
    const form = document.createElement('form');
    form.action = url;
    form.method = method;
    form.target = '_self';
    for (const [name, value] of Object.entries(fields)) {
      const input = document.createElement('input');
      input.type = 'hidden'; input.name = name; input.value = value == null ? '' : String(value);
      form.appendChild(input);
    }
    document.body.appendChild(form);
    // A field called "submit" must not shadow the native method.
    HTMLFormElement.prototype.submit.call(form);
  }

  function install3dsHandling(api, { onChallenge = () => {}, onError = () => {}, navigate = navigateChallenge } = {}) {
    const request = api.request.bind(api);
    api.request = function (action, method, params) {
      const pending = request(action, method, params);
      if (action === 'api.checkout.form' && method === 'request') {
        let redirected = false;
        // PaymentButton 2.0.15 forwards done/fail but omits progress. The API emits
        // submit3ds as progress, so handle it on this dedicated API instance.
        pending.progress(function (model) {
          if (redirected) return;
          try {
            const challenge = challengeRequest(model);
            redirected = true;
            onChallenge();
            navigate(challenge);
          } catch (error) { onError(error); }
        });
      }
      return pending;
    };
    return api;
  }

  const exported = { createFlow, challengeRequest, install3dsHandling };
  if (typeof module !== 'undefined' && module.exports) module.exports = exported;
  else root.WalletDemo = exported;
})(globalThis);
