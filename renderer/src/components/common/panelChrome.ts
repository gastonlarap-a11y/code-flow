/**
 * The chrome every panel wears: rounded card, one border, one shadow.
 *
 * It used to be half of a split (`docs/UX-REDESIGN.md` §II.4): views wore this, while docked asides
 * — the sidebar, the AI panel, the terminal dock — stayed flush, full height and square, because
 * they touched the window edge. Phase 3 of `docs/REDESIGN-PROPOSAL.md` (§4.6) closed that split:
 * nothing touches the window edge any more. The navigation sidebar, the context panel, the active
 * view, the AI panel and the terminal are all cards with space between them, floating over the
 * ambient gradients — which is what those gradients were strengthened for in Phase 1, since panels
 * that covered them left almost nothing to see.
 *
 * It lived in `components/api/` until the Editor and Graph views turned out to be re-typing the
 * identical string by hand in five places, which is the usual way two surfaces drift a pixel apart.
 * It is in `common/` now so there is one copy to change.
 */
export const CARD =
  "rounded-xl border border-[var(--cf-border)] bg-[var(--cf-surface)] shadow-[var(--cf-shadow)]";
