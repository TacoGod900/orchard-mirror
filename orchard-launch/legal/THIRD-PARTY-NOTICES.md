# Third-Party Notices & Open-Source Compliance

> This is the most legally *load-bearing* document here, because it's not a template you can
> ignore — it's a licence obligation you take on the moment you distribute the app. The others are
> drafts a lawyer should review; this one is concrete compliance you must actually do. Read the
> "What you must do" box.

Orchard Mirror is built on open-source components. Some carry copyleft licences that impose real
obligations on anyone who distributes the software — including you, the moment you hand a build to a
beta user or sell it.

## Components and their licences

| Component | Role | Licence | Obligation |
|---|---|---|---|
| **pymobiledevice3** | The Python agent that talks to the iPhone (CoreDevice, tunnel, HID) | **GPL-3.0** | Strong copyleft. See below. |
| Patched **UxPlay** (prototype only) | Historical AirPlay receiver | **GPL-3.0** | Corresponding source of your modifications must be offered. |
| .NET runtime & libraries | App framework | MIT / Apache-2.0 | Attribution only. |

## The GPL-3.0 obligation (this is the important part)

pymobiledevice3 is licensed under **GPL-3.0**. Orchard Mirror's own decision record (ADR 0014)
deliberately runs it as a **separate process** so that the C# application is not a derivative work
and does not itself become GPL. **That boundary only holds if you keep it.** To stay compliant when
you distribute Orchard Mirror:

1. **Keep pymobiledevice3 in its own process.** Do not statically or dynamically link it into the C#
   app, copy its source into your codebase, or merge the two. The stdio-line-protocol boundary in the
   agent is what preserves this — leave it intact.
2. **Provide the agent's source.** Because you distribute the GPL agent (bundled `agent.py` plus the
   pymobiledevice3 it imports), you must give recipients the corresponding source and the GPL-3.0
   licence text, or a **written offer** to supply it. A link in the app's About screen and on the
   website to the pymobiledevice3 source and to your `agent.py` satisfies this.
3. **Do not remove or obscure its licence and copyright notices.**
4. **The C# app can be proprietary and paid** — that's the whole point of the separate-process
   design — but the **agent remains GPL** and its source must be available.

### Written offer (put this in your About screen and website footer)

> Orchard Mirror includes pymobiledevice3 and a Python agent licensed under the GNU General Public
> License v3.0. The complete corresponding source code is available at
> [YOUR REPO / DOWNLOAD LINK]. A copy of the GPL-3.0 licence is included with this software.

## What you must do before distributing

- [ ] Bundle the full text of GPL-3.0 with the app (a `LICENSE-GPL.txt` next to the agent).
- [ ] Publish the exact `agent.py` you ship and a pointer to the pymobiledevice3 version/commit.
- [ ] Add the written offer above to the app's About screen and the website footer.
- [ ] Confirm with a lawyer that your separate-process boundary is intact **before selling** — this
      is the single highest-risk legal item, because getting it wrong can make your paid C# app
      subject to GPL too.

> **Not legal advice.** GPL compliance is nuanced and the separate-process argument, while sound in
> your ADR, is a legal position, not a certainty. Have a lawyer confirm it before you take money.
