# Flitt one-button wallet demo

.NET 8 minimal web API + Flitt PaymentButton SDK 2.0.15. Orders deliberately live
in memory for testing; no database or production ledger.

## Run

```sh
dotnet run --project Demo.csproj --urls http://localhost:5078
cloudflared tunnel --no-autoupdate --protocol http2 --url http://localhost:5078
```

Open the HTTPS tunnel with `?wallet=google` or `?wallet=apple`.
For local Google Pay UI testing, the original demo observed that
`http://wallet.localhost:5078` works where bare localhost received an iframe 403.
That is a test observation, not an exemption from domain registration.

The configured MID 1549901 / secret "test" are Flitt's public test credentials.
Never commit live secrets; override via `Flitt__MerchantId`, `Flitt__SecretKey`
and `Flitt__CallbackUrl` environment variables.

## Order gate: both wallets

1. Typing updates the amount; it does not create an order.
2. Click starts `POST /api/orders` in `before`. The hook resolves synchronously
   so the native wallet opens within the click gesture.
3. Backend inserts our in-memory order FIRST, then requests Flitt's checkout
   token using that ID, amount, GEL, lifetime=900, delayed=N, callback and return URLs.
4. After wallet authorization, `after` waits for that same backend request.
   Only a valid order ID + token releases the final SDK payment submission.
   Failure, missing token, cancellation, or an amount mismatch rejects the hook.
5. Flitt's signed callback updates our existing order. Neither a browser return
   nor the native wallet success display marks our order paid.

One page permits one attempt. Use "Start a new payment" to reload for another.
A cancelled wallet can leave an unpaid order. There are no automatic charge retries.
A network error AFTER payment submission is an unknown outcome, not proof of no charge.

Hooks must be plain functions: SDK 2.0.15 ignores directly supplied async functions.
The plain wrappers delegate to asynchronous work.

## Google Pay 3DS

Per Flitt's notice, gambling merchants in Georgia may use this integration on
the web, not inside Android apps. Do not embed the demo as an Android-app payment flow.

Device-tokenized cards can complete without a challenge; Google-account-only cards
require 3DS under the supplied notice. Follow the gateway response rather than
guessing card type or forcing 3DS on all cards.

SDK 2.0.15 emits `submit3ds` as API deferred progress, which PaymentButton does
not forward. Our dedicated API adapter handles that notification and navigates
the whole page to the HTTPS challenge, preserving Flitt's URL, method, challenge
fields and TermUrl. Do not use `prepare3dsData()` for this flow: it rewrites
TermUrl for an iframe. Normal SDK redirect/form responses are handled too.

The configured browser return is `/payment-return?orderId=...` (GET or POST).
It ignores payment-result parameters and redirects to the demo's status page.
The page displays callback state and performs one gateway status lookup.
This is not a background reconciliation service.

This adapter depends on the inspected pinned SDK response shape. Recheck on upgrade.
A live non-tokenized-card challenge and return still need verification with a
Flitt-enabled MID/domain; mocked tests do not establish live payment acceptance.

Apple caveat: this SDK calls completePayment(STATUS_SUCCESS) BEFORE the after hook.
The sheet may show success before our backend finishes. Ask Flitt for the supported
approach/version that reports the actual outcome; use the page's confirmed result.

## Domain and merchant setup

Send Flitt every Google Pay hostname mapped to its MID, including relevant staging,
production and subdomains. Wait for their explicit configuration/activation confirmation.

For Apple Pay, obtain the association file from Flitt and put it at the project root as
`apple-developer-merchantid-domain-association.txt`. The app serves it at
`/.well-known/apple-developer-merchantid-domain-association`. Ask Flitt to register
the HTTPS domain. The file is not included here.

Quick Cloudflare tunnels change hostname on restart. They are temporary test links
only where Flitt permits that origin, not automatically registered wallet domains.
Use a stable named tunnel or staging hostname for registered wallet testing.

Callback and return URLs use the public request host and forwarded HTTPS scheme
in this demo. The tunnel must preserve those headers. Keep it running for callbacks.

## Ask Flitt about ongoing ownership and renewals

- Do you own/renew Apple's merchant identity and payment-processing certificates?
  Do we need our own Apple Developer account, annual membership, Merchant ID or
  certificates? Give the exact expiry schedule and renewal owner for each item.
- Does the Apple domain verification/association file expire or require replacement?
  What must be repeated after changing DNS, CDN, hosting or HTTPS certificates?
- Do you manage Google merchant registration/production approval, or do we need
  a Google Pay & Wallet Console account and merchant ID? Any periodic reapproval?
- Confirm all staging/live hostname-to-MID mappings, subdomain rules and whether
  temporary tunnel domains are allowed.
- Any recurring fees, SDK upgrade requirements or configuration maintenance?
  Who receives expiry/change notices?
- Confirm the SDK 3DS progress payload and top-level return flow; provide a
  Google-account-only card test case requiring a challenge.
- Can the MID require backend-created tokens? Confirm that rejecting after cannot
  fall back to an unsigned client-created order.
- Which supported SDK version fixes Apple native success appearing before payment?

Do not assume everything renews annually: Flitt must confirm ownership and schedules.
We maintain our domain, DNS, HTTPS, hosting/tunnel and association-file endpoint.

## Verification

```sh
dotnet build --ignore-failed-sources
node --test tests/wallet-flow.test.cjs
```

Tests cover the order gate, failure/cancellation/amount races, overlapping clicks,
and 3DS progress/form handling. They do not charge real cards.

References:
- https://docs.flitt.com/api/googlepay-web/
- https://docs.flitt.com/sdk-and-mobile/sdk/wallets/
- https://docs.flitt.com/api/callbacks/
- https://cdn.jsdelivr.net/npm/@flittpayments/js-sdk@2.0.15/dist/umd/checkout.js

The email URL ending in /googlepay-web/3 was inaccessible; the canonical
/googlepay-web/ page and actual pinned SDK were inspected.
