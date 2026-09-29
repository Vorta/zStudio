## Problem and resulting behavior

Describe the concrete problem and what changes for users. Link the related issue.

## Verification

- [ ] Release build and asset-independent tests pass.
- [ ] Relevant regressions or malformed-input cases are covered where behavior changes.
- [ ] No game assets, extracted content, build output or credentials are included.
- [ ] User-facing documentation/changelog is updated when applicable.
- [ ] New/changed GUI capabilities have MCP parity, typed schemas, capability inventory entries and protocol verification (or an explained presentation-only equivalent).

List local corpus/UI checks run, checks not run, and any remaining limitations. Include synthetic reproductions where possible; do not attach game archives.

## Adversarial review

Follow [the review procedure](../docs/code-review.md). For documentation-only changes, state that code risk passes are not applicable and list document checks.

- [ ] The full PR diff and affected producers/consumers were reviewed; relevant risk passes have concrete evidence and any coverage gaps are stated.
- [ ] Findings were reproduced or supported by a concrete failing path, fixes were rechecked, and a final challenge pass after the last production change found no further actionable P1/P2. List unresolved findings instead of checking this box if any remain.

Summarize the reviewed revision/scope, adversarial cases, findings fixed, and remaining limitations. Passing build/tests alone does not complete this review.
