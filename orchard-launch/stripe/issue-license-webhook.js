// Orchard Mirror — Stripe webhook that issues a license key on purchase.
//
// This is the FOUNDATION, not a finished backend. It's a Node serverless function (works on
// Vercel, Netlify, Cloudflare Workers with minor tweaks, or a tiny Express server) that:
//   1. verifies the request really came from Stripe,
//   2. on a completed checkout, maps the product to a license tier,
//   3. generates a key in the EXACT format the app validates (see src/Orchard.Mirror.Shell/Licensing.cs),
//   4. hands it to you to email + store.
//
// The key generator below is a line-for-line match of the C# `Licensing` class, so a key this
// server issues will parse and be accepted by the app offline. Keep the two in sync: if you change
// the alphabet or the check in one, change it in both, or issued keys stop validating.
//
// WHAT'S STUBBED (do these when you wire it up): storing issued keys, sending the email, and mapping
// your real Stripe Price IDs to tiers. Search for TODO.

const crypto = require("crypto");
const Stripe = require("stripe");

const stripe = new Stripe(process.env.STRIPE_SECRET_KEY); // test key while in test mode
const WEBHOOK_SECRET = process.env.STRIPE_WEBHOOK_SECRET;

// ---- Key generation: MUST match src/Orchard.Mirror.Shell/Licensing.cs ----
const ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I — reads aloud cleanly
const TIER_CODE = { beta: "BETA", monthly: "MON", yearly: "YEAR", lifetime: "LIFE" };

function randomData() {
  // 8 characters from the shared alphabet.
  const bytes = crypto.randomBytes(8);
  let out = "";
  for (const b of bytes) out += ALPHABET[b % ALPHABET.length];
  return out;
}

function check(code, data) {
  const hex = crypto.createHash("sha256").update(`ORCH-${code}-${data}`).digest("hex").toUpperCase();
  return hex.slice(0, 4);
}

function createKey(tier) {
  const code = TIER_CODE[tier];
  if (!code) throw new Error(`Unknown tier: ${tier}`);
  const data = randomData();
  return `ORCH-${code}-${data}-${check(code, data)}`;
}

// ---- Map your Stripe Price IDs to tiers. TODO: paste your real (test-mode) price IDs. ----
const PRICE_TO_TIER = {
  "price_TEST_monthly": "monthly",
  "price_TEST_yearly": "yearly",
  "price_TEST_lifetime": "lifetime",
};

// ---- The webhook handler ----
module.exports = async function handler(req, res) {
  let event;
  try {
    // req.body must be the RAW body for signature verification (configure your platform for this).
    event = stripe.webhooks.constructEvent(req.rawBody, req.headers["stripe-signature"], WEBHOOK_SECRET);
  } catch (err) {
    return res.status(400).send(`Webhook signature verification failed: ${err.message}`);
  }

  if (event.type === "checkout.session.completed") {
    const session = event.data.object;
    const email = session.customer_details?.email;

    // Figure out which product was bought.
    const lineItems = await stripe.checkout.sessions.listLineItems(session.id, { limit: 1 });
    const priceId = lineItems.data[0]?.price?.id;
    const tier = PRICE_TO_TIER[priceId];

    if (!tier) {
      console.error(`No tier mapped for price ${priceId}`);
      return res.status(200).send("ok (no tier mapped)");
    }

    const licenseKey = createKey(tier);

    // TODO: persist { email, tier, licenseKey, stripeCustomerId, createdAt } to your store,
    //       so you can verify/revoke later and so a refund can deactivate the key.
    // TODO: email the key to `email` (Resend, Postmark, SendGrid, etc.).
    console.log(`Issued ${tier} key ${licenseKey} to ${email}`);
  }

  // TODO later: handle `charge.refunded` and `customer.subscription.deleted` to deactivate keys.

  return res.status(200).send("ok");
};

// Quick self-check: node issue-license-webhook.js  -> prints one key of each tier so you can paste
// them into the app and confirm they're accepted.
if (require.main === module) {
  for (const tier of Object.keys(TIER_CODE)) {
    console.log(`${tier.padEnd(9)} ${createKey(tier)}`);
  }
}
