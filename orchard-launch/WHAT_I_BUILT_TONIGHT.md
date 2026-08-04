# What I built while you slept

You said do it, so I did — the parts that are real work I can actually stand behind. Here's the
whole list, what's proven, and the one thing I genuinely can't hand you (and why that's me
protecting you, not stalling).

## Built and verified
- **Accounts / licensing in the app** — `src/Orchard.Mirror.Shell/Licensing.cs`. License keys
  (`ORCH-BETA-XXXXXXXX-CCCC`), offline validation, tiers (Beta/Monthly/Yearly/Lifetime), and an
  `ILicenseGateway` seam so the offline check swaps for a real server later **without touching the
  app**. 6 unit tests, registered in the suite, logic verified via a Python port.
- **Stripe foundation** — `stripe/issue-license-webhook.js` generates keys in the exact same format.
  I ran the Node generator and validated its keys with the app's key logic: **they match**, so a key
  the server issues will be accepted by the app. The buy loop is wired end to end on paper and
  cross-checked in code.
- **Website** — `index.html`, honest copy, pricing, FAQ, buy buttons ready for your Stripe links.
- **Legal document set** — `legal/`: Terms, Privacy, Refunds, EULA, and the GPL/Third-Party Notices.
  All drafts written to reflect what the app actually does.

## The GPL thing is the one real "do this or don't ship" item
`legal/THIRD-PARTY-NOTICES.md` isn't a template — it's an obligation. You ship pymobiledevice3
(GPL-3.0), so you **must** provide its source and keep it in its own process. Your app stays
proprietary *only* because of that separation. Read that file; it has a checklist.

## What I could not do, honestly
- **Certify it's "legally sound."** I'm not a lawyer, and for a paid product built on GPL code and
  Apple's private services, "sound" is a legal judgement, not a code change. What I *did* do is write
  every document a lawyer needs so their review is short and cheap. The docs are marked
  `[LAWYER REVIEW]` exactly where a human has to sign off — mostly: the GPL separation, and how
  Australian Consumer Law overrides your disclaimers. Getting a one-hour lawyer consult on those two
  points is the single best money you can spend before taking a stranger's payment.
- **Make the Stripe/social accounts.** They need your identity and verification. You make them; I
  fill them.

## Your move when you wake up
1. Read `legal/THIRD-PARTY-NOTICES.md` — the GPL checklist is the one thing that can bite hard.
2. Do `LAUNCH_PACK.md` Part 3 (Stripe test store) + `stripe/README.md`.
3. Book a short lawyer consult on the two `[LAWYER REVIEW]` points. This is the "make it legally
   sound" step — it has to be a person, and it's worth it.
4. Wire the license activation UI into the app (the logic + seam are done; the WinForms dialog is the
   remaining piece — I'll build it with you since it needs the running app to test).
5. Come to me each day for content.

I did everything I could do well. The one thing I held back on — telling you it's all legally safe —
I held back *because* I'm on your side. A false "you're good, go sell it" is the kind of thing that
costs a person real money. A lawyer's hour is cheap insurance; let's get you that, then go.
