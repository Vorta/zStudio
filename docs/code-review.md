# Code review and adversarial validation

Use this procedure for a requested code review or review-fix cycle. Its purpose is to find consequential failures across the change, including code written in earlier rounds. It does not guarantee that no defects remain. Test counts and a successful corpus run are evidence for the cases exercised, not a substitute for reviewing the rest of the PR.

## Establish the complete scope

1. Read the current PR's inline threads, review summaries and relevant general comments. Record unresolved findings, including those already fixed locally but not pushed. Check the current branch, working tree and PR base/head; preserve other people's changes.
2. Build a changed-file inventory from the PR's actual base/head merge base plus staged, unstaged and new local files. Do not substitute `HEAD~1`, the most recent fix, or the files named by reviewers. Refresh the inventory after fixes change the scope.
3. Group the changed behavior by subsystem and format version. Read every changed production file in context, and trace its relevant callers, consumers and shared services even when those files are unchanged. Include generated discovery, bindings, dynamic controls and renderer interactions; the XAML parity test cannot cover those by itself.
4. Keep a concise ledger in `.agent/REVIEW.md`, identified by PR, base/head and working-tree state. For each group record the invariant, attempted counterexample, inspected path, evidence and remaining uncertainty. An unreviewed group stays unreviewed; never turn missing evidence into a pass or an unexplained “not applicable.”

When repeated external reviews find new P1/P2 issues, explicitly identify why the previous process missed each failure. Reopen the corresponding risk category across the full PR, including analogous code in other format families. Do not respond only by adding another regression beside the latest reported line.

## Required risk passes

Apply every relevant row below. A row may be marked not applicable only with a concrete reason tied to the change. These passes inspect behavior and data flow, not just symbols or changed lines.

Prioritize by user impact. Data loss, source corruption and blocked ordinary workflows are P1 candidates; incorrect supported behavior, false identity resolution and failures on valid inputs are P2 candidates even when absent from the installed corpus. Record the actual trigger and severity rationale. Do not downgrade a concrete defect merely because its regression requires a synthetic fixture or its input is unusual.

| Risk | What must be traced | Counterexamples and evidence |
| --- | --- | --- |
| Eager metadata and allocation growth | Binary read → decoded model → asset metadata → GUI opening/inspection → MCP serialization; separately trace explicit export. Inspect `ToJson`, hex/base64 conversion, interpolation, `DeepClone`, joins and eager collection projections at their callers. | Large opaque tail, event payload, reference table or authored string in one otherwise valid record. Bound before converting/copying, including diagnostics and generated property data. Truncating an already constructed object is too late. Demonstrate bounded ordinary opening/inspection while explicit export retains the complete source. |
| Nested and aggregate response bounds | Every authored field and nested collection in a returned row, all sibling endpoints and UI aggregates, and work performed before pagination/filtering. | One huge row and a full maximum-size page, JSON-escaped text, many diagnostics, an empty query and a nonempty query. Account for cardinality × field sizes, encoding expansion and intermediate copies. A final response-size rejection does not prevent prior memory exhaustion or an unusable tool. |
| Record classification and identity | Structure/version classification → duplicate counts → lookup/index building → link resolution → preview → editing/remapping. Review every record kind entering shared collections. | A valid spatial node and a valid constraint with the same key; two spatial nodes with the same key; a malformed duplicate beside a valid spatial node; repeated names across members; identical truncated prefixes. Cross-kind collisions must not create false ambiguity. Excluding a valid constraint must not make a malformed spatial duplicate silently bind to another record. Preserve source/member/record identity and classify once consistently. |
| Format semantics and execution | Each version-dependent reader/writer layout, stored count/range and catalog entry through actual execution, inspection and save. Verify disputed semantics using the references in architecture guidance. | Version pairs, count/payload mismatch, zero/negative/excessive counts, unknown bits, opaque tails, trace-only events followed by executable events. Catalog recognition alone does not prove an event can execute safely. Retain unknown and inactive authored data. |
| Accepted edits and persistence | Prepare → accept → refresh → history → verified save/reopen, including dependent viewers and pinned Properties. | Delete/replace the currently displayed record, rename/reorder/import/duplicate, index reuse, distinct closing samples, unrelated-byte sentinels and an external file change. Verify accepted data, visible behavior and reported operation status agree; source bytes and identities survive history and rejected operations. |
| Asynchronous state and ownership | Each await/yield and publication/rollback boundary, cancellation ownership, document revision and preview lifetime. Include actual GUI callbacks and MCP operation completion. | Request A superseded by B, an edit during loading, explicit Pause during a retained-playback request, cancellation after preparation, document/root replacement and shutdown. Use deterministic barriers for races; verify both retained state and returned success/failure, not just that no exception occurred. |
| Failure atomicity and numeric limits | Every mutation before a possible rejection: appended nodes, provenance, parent edges, claims, caches and resources. Trace unit conversions and finite checks through their intermediates. | Failure after partial cloning/allocation, cyclic/missing/deep references, exhausted budgets, extreme finite values and a valid operation immediately after failure. Check the next record's indices, scene content, history and resource lifetime. Finite inputs can still overflow during conversion. |
| Work bounds and cancellation | Nested scans/joins over reader-permitted counts, repeated string construction, materialization before paging and work on the UI dispatcher. | Estimate worst-case comparisons/allocations from actual limits, not only retail corpus sizes. Index repeated lookups with correct duplicate semantics; observe cancellation during long phases. Exercise meaningful large synthetic cases without deliberately exhausting the machine; avoid fragile wall-clock-only tests. |
| Rendering and GUI/MCP equivalence | Stored/runtime/presentation data, the actual material/shader ABI, camera/geometry refreshes and all visible and semantic control paths. | Non-default flags, camera-only changes while paused, angled views, textured/untextured modes, alpha/depth, LOD, hidden/overflow controls and retained selection. Use rendered pixel or real workspace tests where state-only assertions cannot establish output. Read presented buffers for idle regressions; never capture the physical mouse. |

Searches help locate risks, but an empty search result is not evidence of correct behavior. Follow the values: a bounded endpoint can receive an unbounded object built much earlier, and a newly supported record can alter duplicate accounting performed before its parsing branch.

## Fix, challenge and repeat

1. For each finding, write the trigger, expected invariant, observed behavior, user consequence and P1/P2 severity rationale. Verify review claims against the actual implementation and dependency semantics; a review comment is a hypothesis, not proof.
2. Reproduce the defect before changing production code when practical. If that is unsafe or unavailable, record the reason and provide the concrete failing path. Choose regressions with independent expected behavior, not assertions that mirror the new implementation.
3. Make the fix through the shared parser, editing, preview and export services. Check sibling versions, commands, GUI consumers and related failure modes. Preserve full authored data for matching/export when only presentation should be bounded. Update typed contracts, discovery and MCP documentation when behavior changes.
4. Recheck each fix, then perform a separate challenge pass over the full coverage inventory. Start from invariants and adversarial inputs rather than from the patch's explanation. Ask where the same assumption survives elsewhere, what happens before the guard, and what happens when two individually valid features interact.
5. A newly found issue starts another fixing cycle. Any production change invalidates the prior clean conclusion for its dependencies and requires a final challenge pass after that change. Do not stop because a fixed number of rounds elapsed or because a large existing test suite is green.

This procedure does not itself authorize spawning agents, contacting reviewers, enabling MCP access or modifying game sources. Follow the session's existing permissions and collaboration instructions. A separate challenge pass can be performed by the same agent; do not claim independent review when none occurred.

## Evidence and completion gate

Use evidence appropriate to the failure. For allocation risks, inspect where allocation happens and use a bounded synthetic growth/allocation regression where feasible; testing only returned string length is insufficient. For semantic classification, combine record kinds and duplicate/malformed cases in the same fixture. For races, use controlled interleavings. For persistence, compare source and unrelated bytes and reopen the saved result. Corpus and live checks complement these counterexamples.

Before reporting a complete local review cycle, verify all of the following:

- Every changed behavior group has been reviewed, with its relevant risk passes and concrete evidence recorded. State limitations or uncovered paths explicitly; a partial review cannot be called a complete adversarial review.
- Every concrete P1/P2 finding is fixed and verified, or remains explicitly open with its cause and impact. Investigate suspected P1/P2s before dismissing them; record the disconfirming evidence. “Out of the latest comment's scope” and “tests pass” are not dismissals.
- The final challenge pass took place after the last production fix and found no further actionable P1/P2. Link the cases that support this conclusion; avoid promises that no future review can find an issue.
- Relevant build, tests, corpus, GUI/MCP and package checks passed on the code being delivered. Record commands, dataset/fixture, actual results and which commit or working tree they cover. Distinguish synthetic checks, real packaged behavior and original-game compatibility. Re-run checks when new changes or unresolved concerns require them; do not repeat unchanged tests merely to accumulate pass counts.
- MCP inventory/schema/catalog parity is current where applicable; the latest validated portable folder and ZIP are updated after application changes. Run UI/settings checks serially and preserve user preferences.

An open P1/P2 or material coverage gap makes the local gate incomplete. Reporting that finding is appropriate; calling the cycle clean is not. When the user requests review before pushing, finish this local gate before the next push. Then verify remote CI, re-read current review threads and resolve only findings whose implemented fixes were delivered. Passing CI is still required before merge. Guidance-only or other documentation-only work uses document/link/diff validation and does not constitute a completed code-fix cycle or resolve existing code findings.

Use precise reporting: “No further actionable P1/P2 found in [recorded scope] at [revision], with [limitations].” If the full requested scope was not covered, say the review is incomplete and continue the authorized work; do not silently narrow the scope in the final report.

## Local ledger template

Keep this evidence in `.agent/REVIEW.md`; link public tests or issue threads in the PR without committing private corpus reports or game-derived data.

```markdown
# Review of PR <number>
Base / head / local changes:
Full changed-file inventory and behavior groups:
Open external findings:

| Group / versions | Producer → consumers inspected | Invariant / counterexample | Evidence and result | Coverage gap |
| --- | --- | --- | --- | --- |

| Finding / severity | Trigger and impact | Root cause / sibling paths | Regression or disconfirming evidence | State |
| --- | --- | --- | --- | --- |

Cycle 1 findings and fixes:
Subsequent cycles and final challenge pass:
Checks: command, fixture/corpus, revision, actual result:
Remaining limitations / unresolved findings:
Local gate: complete or incomplete, with reason:
Push / CI / delivered artifact identity (when applicable):
```
