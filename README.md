# Flitt one-button wallet demo

Minimal .NET 8 page that shows **one** wallet button (Apple Pay on Apple devices, Google Pay
everywhere else) using Flitt's JS SDK `PaymentButton`. A single tap opens the native sheet.

```
dotnet run --project demo.flitt-wallet-button/Demo.csproj --urls http://localhost:5077
```

Open **http://wallet.localhost:5077** — not `localhost`. Flitt's CDN returns 403 for the SDK's
gateway iframe when `origin` is `localhost` / `127.0.0.1`, and the button never renders.
`*.localhost` passes and Chrome still treats it as a secure context.

`?wallet=apple` / `?wallet=google` forces a wallet.

## Flow

1. Page load → `$checkout.get('PaymentButton', { methods: [oneMethod], data: { merchant_id, amount, currency }, after })`.
   Client-side mode: the button renders with **no order**. Typing calls `paymentButton.update({ amount })`.
2. Tap → native Apple Pay / Google Pay sheet opens immediately (no network call in the tap —
   Apple requires the session to start synchronously inside the gesture). Apple merchant
   validation only needs `merchant_id` + domain, not an order.
3. Payer authorizes → SDK calls our `after(defer, params)` hook → `POST /api/orders` creates our
   transaction + Flitt token → `defer.resolve({ token })` → SDK merges it into the charge request.
   **Pass a plain `function`, not an `async function`**: the SDK's `isFunction()` checks
   `[object Function]`, rejects `AsyncFunction`, and silently uses its default hook — the charge
   then goes out with no order of ours.
4. Flitt → `server_callback_url` (signature-verified in `/flitt/callback`). That is the source of truth.
   The callback URL follows the host the page was opened on, so it works through the tunnel.

## HTTPS tunnel

```
cloudflared tunnel --no-autoupdate --protocol http2 --url http://localhost:5077
```

Prints a `https://<random>.trycloudflare.com` URL. `--protocol http2` because QUIC (UDP) is
blocked on our network and the tunnel never connects without it (Error 1033).
`CallbackUrl` is empty in `appsettings.json`, so the callback URL follows the host the page was
opened on — Flitt's server callback reaches `/flitt/callback` through the tunnel.

The quick-tunnel hostname **changes on every restart**, so it is fine for Google Pay and callbacks
but a poor host to register with Apple. For Apple Pay registration use a stable hostname:
a Cloudflare named tunnel on a domain we own (`cloudflared tunnel login` → `tunnel create` →
`tunnel route dns`), an ngrok reserved domain, or a real staging host.

## Apple Pay — why it doesn't work locally

Apple Pay JS refuses to start on anything but HTTPS on a domain registered for the merchant ID.
Flitt's test merchant uses Flitt's own Apple merchant ID `merchant.flitt`, so to test:

1. Expose the demo on a public HTTPS host (staging domain or a tunnel).
2. Ask Flitt support for `apple-developer-merchantid-domain-association.txt`, put it in this
   folder — it is served at `/.well-known/apple-developer-merchantid-domain-association`.
3. Ask Flitt to activate that domain with Apple.
4. Open in Safari on an iPhone/Mac with a real card in Wallet (test mode converts it to a test token).

Test merchant: `1549901`, secret `test` (Flitt's public sandbox data, `appsettings.json`).
