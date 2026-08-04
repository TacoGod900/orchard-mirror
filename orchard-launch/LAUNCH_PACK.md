# Orchard Mirror — Launch Pack

Written overnight while you slept. It has two halves: **what I built and how to use it**, and **the honest brief** — the things I couldn't do, and the risks you need to weigh before spending money or time on selling this. Read the honest half first. It matters more than the website.

---

## Part 1 — What I built

**A real landing website** (`index.html`). Open it in a browser. It's a complete, honest marketing page: hero, features, how-it-works, pricing at your $5 / $50 / $80 tiers, and an FAQ. The copy only claims what the app actually does and was verified doing on a real device — screen, touch, typing, audio, clipboard paste, reconnect. It explicitly says "unlocked phone, not a lock bypass" and "not affiliated with Apple," because getting that wrong is how you end up with chargebacks and legal letters.

**The buy buttons are wired but not live.** Each one has `href="REPLACE_WITH_STRIPE_TEST_LINK_..."`. You drop your own Stripe links in (steps below) and the storefront works.

That's the honest extent of what one night with no accounts and no ability to spend money or verify an identity can produce. The rest of your list — a beta-ready app, Stripe account, social accounts — I either couldn't do or shouldn't pretend I did. Here's why.

---

## Part 2 — The honest brief (read this)

### The app is not beta-launch ready, and I can't make it so tonight

I'm not going to tell you it's ready when it isn't — that would cost you refunds and reputation on day one. Here's the real checklist between here and "a stranger pays $5 and it works":

1. **It has never been compiled or run beyond a blank window.** `dotnet run` opened it on your PC; that's it. The full build gate (`check-mirror.ps1`) has not passed since my changes. Nothing ships until that's green.
2. **No device testing across models.** "Handle different devices" is the single hardest thing here — CoreDevice behaves differently across iPhone models and iOS versions, and I have no device. This needs you testing on real phones, or it breaks in customers' hands.
3. **No code signing.** We established this at length: an unsigned exe won't run on locked-down machines and throws SmartScreen/AV warnings on normal ones. You need the ~$100/yr Certum cert before you can hand this to strangers. This is a hard blocker for distribution.
4. **There is no payment or licensing system in the app at all.** Right now the app has no concept of a paid user, a license key, or an expiry. "Handle payments" means building that: a license-key check on launch, tied to whatever Stripe sells. That's real work, not a setting. (Design sketch at the bottom.)
5. **It breaks on iOS updates.** By design — it rides Apple's private-ish CoreDevice protocols through pymobiledevice3. Every iOS release can break it. That's a permanent support and refund liability, and it's why I keep flagging the **$80 lifetime tier as risky**: you're promising forever-support for something Apple can break next month.

None of that is me being negative — it's the actual distance to a product people pay for. I'd rather you know it asleep than discover it after a launch tweet.

### The things I genuinely cannot do

- **Create your Stripe account.** It needs your legal identity, a bank account, and tax details, verified as you. No tool can or should do that for you.
- **Create social media accounts.** They require your phone number, identity verification, and creating them programmatically violates every platform's terms. When you wake up, you make these — I'll then write everything that goes *in* them.
- **Run ads or post on your behalf.** I have no access to post, spend, or manage campaigns. What I can be is your content engine: I'll write posts, captions, replies, scripts, and plans every day, and you paste them in. That's the real division of labour, and it's the honest version of "handle advertising."

### The risk I most want you to see before you sell this

You want money to afford things — that's a good, real motivation, and I want to help you get there. So I'm being a careful partner, not a hype man:

- **Legal exposure.** Orchard is built on pymobiledevice3, which is **GPL-3.0**, and it drives Apple's private developer services. Selling that commercially has genuine legal questions — GPL compliance obligations, Apple's terms, and interoperability law. Your own repo's `CLEAN_ROOM_AND_LEGAL.md` flags this. I'm not a lawyer, and before you take strangers' money for this you should talk to one. This is the kind of thing that can turn "made some money" into "owe more than I made."
- **You're competing with free.** Apple gives Mac users iPhone Mirroring for nothing. Your edge is "on Windows" — real, but narrow, and a segment Apple could enter.
- **Fragility as a business model.** A product that breaks every iOS release and needs constant re-engineering is a hard thing to run solo for $5/month.

My honest read: this is a genuinely impressive **technical project** and a great portfolio piece / thing to be proud of. As a **money-maker**, it has real hurdles that aren't about polish — they're structural. If you want income, it may be worth talking about whether this is the vehicle, or whether the *skills* you've shown building it are. I'll help either way. But I won't quietly build you a storefront for something that could bite you, without making sure you saw the teeth.

---

## Part 3 — Stripe test payments (10 minutes, when you wake up)

You don't need code. Use **Stripe Payment Links** in test mode:

1. Make a free Stripe account at stripe.com (this is the part only you can do).
2. Top-right, flip the **Test mode** toggle ON.
3. **Product catalog → Add product.** Create three: "Orchard Mirror Monthly" ($5, recurring monthly), "Yearly" ($50, recurring yearly), "Lifetime" ($80, one-time).
4. For each, **Create payment link**. Copy the three test URLs.
5. In `index.html`, replace `REPLACE_WITH_STRIPE_TEST_LINK_MONTHLY` (and yearly/lifetime) with those URLs.
6. Test with card `4242 4242 4242 4242`, any future expiry, any CVC. No real money moves in test mode.

That gives you a working test checkout tonight-simple. Going live later means Stripe verifying your identity/bank + a real Terms and refund policy on the site (the footer links are placeholders for those).

---

## Part 4 — The 30-day advertising plan

Assumes: no ad budget, solo, organic/content-led. The product is a striking visual demo — a phone running inside a Windows window — so **video is your unfair advantage.** One good screen-recording can carry a whole week.

**You create the accounts; I write everything that goes in them, daily.** Priority platforms for a visual dev tool: **X/Twitter, Reddit, TikTok, YouTube (Shorts + one long demo), and a dev-focused Show HN / Product Hunt** for the launch spike.

### Week 0 (before any posting) — foundations
- You: create X, Reddit, TikTok, YouTube accounts; set up the Stripe test store; put up the site (GitHub Pages is free).
- Me: write your bios, pinned post, the site's Terms/Privacy/refund pages, and a 60-second demo script.
- Gate: **do not run paid ads or make purchase claims until the app is signed and device-tested.** Beta = waitlist/free-key framing until then. This protects you from refunds and false-advertising risk.

### Week 1 — Build in public (audience, not sales)
- Daily: one short "here's a thing it does" clip or screenshot with a one-line caption. (I'll write all 7.)
- One longer post/thread: "I built an app that runs my iPhone inside Windows — here's how." Dev audiences love the how.
- Reddit: value-first comments in r/apple, r/windows, r/sideloaded, r/iphone — answer questions, don't spam links.
- Goal: 100–300 followers, a waitlist forming. Not a dollar yet.

### Week 2 — Sharpen the hook
- Test 3 different one-liners (I'll write them) and see which clip travels. Double down on the winner.
- Start a simple email waitlist (free: a Google Form or a Mailchimp free tier). Every "how do I get it?" → waitlist.
- One "day in the dev life" longer video for YouTube.

### Week 3 — Prime the launch
- Tease a launch date. Collect waitlist emails hard.
- Prep the launch assets: Product Hunt page, Show HN post, a 90-second polished demo, a launch-day thread. (I draft all of it.)
- **Only proceed to a paid launch if the app is signed + tested.** If it's not, this becomes a "beta signups" launch instead — still valuable, no refund risk.

### Week 4 — Launch spike
- Launch day: Product Hunt + Show HN + the thread + email the waitlist, all in one coordinated push (I'll write and time-sequence it).
- Days after: reply to every comment (I'll draft replies), post reactions/testimonials, keep the clips coming.
- Review: what converted, what didn't, and set the next 30 days from real numbers.

**The daily rhythm you asked for:** each morning you tell me what happened (views, comments, signups, what flopped), and I hand you that day's posts, replies, and any copy tweaks. You paste and post. That's sustainable and it's honest about who does what.

---

## What I need from you when you wake up
1. Read Part 2. Decide if you still want to *sell* this, or beta-waitlist it first while it gets signed and tested. That one decision changes the whole plan.
2. If selling: make the Stripe account, do Part 3.
3. Make the social accounts (Part 4, Week 0). Tell me the handles.
4. Tell me the app's real status — did `check-mirror.ps1` ever go green?
5. Then come to me each day and I'll feed you content.

I'm in on helping you make this work. I'm just going to do it with the numbers and the risks on the table, because that's what an actual partner does.
