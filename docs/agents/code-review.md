# Code review

Procedure for a requested code review or review-fix cycle. It finds consequential failures across the whole change, including code written in earlier rounds; it does not guarantee that none remain. Green tests and a corpus run are evidence only for the cases they exercise.

## Review cycle

- A requested review cycle uses adversarial subagent reviewers: fix their findings, then review again with fresh reviewers, as long as new findings come up (owner decision). A later round reviews what changed since the previous round, not areas that already passed.
- Split a large change into reviewer groups by subsystem; give each reviewer the scope, the invariants to attack and the evidence rules below; reviewers are read-only.
- Reviewers judge zStudio's behaviour on retail data and realistic editing; they do not read the engine reconstruction/decompilation projects or Binary Ninja databases.
- This procedure itself never authorizes contacting outside reviewers, enabling MCP access or modifying game sources. Never claim independent review when none happened.

## Scope

1. Read the PR's inline threads, review summaries and relevant comments; record unresolved findings, including ones fixed locally but not pushed. Check the branch, working tree and PR base/head; preserve other people's changes.
2. Build the changed-file inventory from the actual base/head merge base plus staged, unstaged and new files. Never substitute `HEAD~1`, the latest fix or the files reviewers named. Refresh it when fixes change the scope.
3. Group changed behaviour by subsystem and format version. Read every changed production file in context and trace its callers, consumers and shared services, changed or not, including generated discovery, bindings, dynamic controls and renderer interactions.
4. Keep a ledger in `.agent/REVIEW-<topic>.md` (template below): per group the invariant, attempted counterexample, inspected path, evidence and remaining uncertainty. An unreviewed group stays unreviewed; never turn missing evidence into a pass or an unexplained "not applicable".
5. When later reviews find new P1/P2 issues, record why the earlier pass missed each and reopen that risk category across the whole change, including analogous code in other format families.

## Required risk passes

Apply every relevant row; mark one not applicable only with a concrete reason. Severity: data loss, source corruption and blocked ordinary workflows are P1 candidates; incorrect supported behaviour, false identity resolution and failures on valid input are P2 candidates; block merges only on defects retail data or realistic editing can reach (owner decision). Record the trigger and the severity rationale.

| Risk | Trace | Counterexamples and evidence |
| --- | --- | --- |
| Data growth and allocation | Binary read → decoded model → asset metadata → GUI opening/inspection → MCP serialization; explicit export separately. `ToJson`, hex/base64, interpolation, `DeepClone`, joins and eager projections at their callers. | A large opaque tail, event payload, reference table or authored string in one valid record. Bound before converting or copying, including diagnostics and generated property data; truncating a built object is too late. Show bounded ordinary opening while explicit export keeps the complete source. |
| Response bounds | Every authored field and nested collection of a returned row, sibling endpoints, UI aggregates, work before paging or filtering. | One huge row and a full maximum page, JSON-escaped text, many diagnostics, empty and nonempty queries; cardinality × field size × encoding expansion. A final size rejection does not prevent earlier memory exhaustion. |
| Record classification and identity | Structure/version classification → duplicate counts → index building → link resolution → preview → editing. | A valid spatial node and a valid constraint with the same key; two spatial nodes with one key; a malformed duplicate beside a valid node; repeated names across members; identical truncated prefixes. Cross-kind collisions never create false ambiguity; same-kind malformed duplicates stay ambiguous. Classify once, consistently. |
| Format semantics | Each version-dependent layout, stored count/range and catalog entry through execution, inspection and export. | Version pairs, count/payload mismatch, zero/negative/excessive counts, unknown bits, opaque tails, trace-only events before executable ones. Catalog recognition does not prove safe execution. Keep unknown and inactive authored data. |
| Accepted edits and persistence | Prepare → accept → refresh → history → save → reopen, including dependent viewers and the pinned Properties window. | Delete or replace the displayed record, rename/reorder/duplicate, index reuse, unrelated-byte sentinels, an external file change. Accepted data, visible behaviour and reported status agree; source bytes and identities survive history and rejected operations. |
| Asynchronous state | Each await, publication and rollback boundary, cancellation ownership, document revision, preview lifetime, GUI callbacks and MCP operation completion. | Request A superseded by B, an edit during loading, Pause during retained playback, cancellation after preparation, document or root replacement, shutdown. Use deterministic barriers; check retained state and returned status, not only the absence of exceptions. |
| Failure atomicity and numeric limits | Every mutation before a possible rejection: appended nodes, provenance, parent edges, claims, caches, resources; unit conversions and finite checks through intermediates. | Failure after partial cloning, cyclic/missing/deep references, exhausted budgets, extreme finite values, a valid operation right after a failure. Finite inputs can still overflow during conversion. |
| Work bounds and cancellation | Nested scans and joins over reader-permitted counts, repeated string building, materialization before paging, work on the UI dispatcher. | Worst cases from the actual limits, not retail sizes; indexed repeated lookups with correct duplicate semantics; cancellation in long phases; no fragile wall-clock-only tests. |
| Rendering and GUI/MCP equivalence | Stored, runtime and presentation data, the material/shader ABI, camera and geometry refreshes, every visible and semantic control path. | Non-default flags, camera-only changes while paused, angled views, textured/untextured, alpha/depth, LOD, hidden/overflow controls, retained selection. Use rendered pixels or real workspace tests where state assertions cannot prove output; read presented buffers for idle regressions. |

Searches locate risks; an empty search result is not evidence. Follow the values: a bounded endpoint can receive an unbounded object built much earlier.

## Fix, challenge, repeat

1. Per finding record trigger, expected invariant, observed behaviour, consequence and severity. A review comment is a hypothesis: verify it against the implementation and dependency semantics.
2. Reproduce before changing production code when practical, otherwise record the concrete failing path. Keep a regression test only when it is must-have ([testing](testing.md#minimal-must-have-tests)), with independent expected behaviour. A finding about a test: remove the test unless it is must-have; track any production defect it exposed separately.
3. Fix through the shared parser, editing, preview and export services; check sibling versions, commands, GUI consumers and related failure modes; update typed contracts, discovery and MCP docs when behaviour changes.
4. Recheck each fix, then run a separate challenge pass over the whole inventory starting from invariants and adversarial inputs, not the patch's explanation: where else does the assumption survive, what happens before the guard, what happens when two valid features interact.
5. A new finding starts another cycle; any production change invalidates the clean conclusion for its dependencies. Never stop because a number of rounds elapsed or a large suite is green.

## Completion gate

- Use evidence suited to the failure: allocation measurements for growth (not returned length), mixed record kinds with duplicates in one fixture for classification, controlled interleavings for races, source and unrelated-byte comparison plus reopen for persistence.
- Before calling a cycle complete: every behaviour group reviewed with its risk passes and evidence, limitations stated; every concrete P1/P2 fixed and verified or explicitly open with cause and impact ("outside the latest comment" and "tests pass" are not dismissals); the final challenge pass ran after the last production fix and found nothing actionable; relevant build, tests, corpus, GUI/MCP and package checks passed on the delivered code, recorded with command, dataset, result and revision; MCP inventory, schema and catalog parity current; the portable folder and ZIP republished after application changes.
- An open P1/P2 or a material coverage gap makes the gate incomplete; report it rather than calling the cycle clean.
- A review-before-push request means finishing this local gate before the next push; then check remote CI, re-read review threads and resolve only findings whose fixes were delivered. Passing CI is still required before merge.
- Documentation-only work gets document, link and diff validation; it is not a code-fix cycle.
- With an armed [PR watch](pr-watch.md), acknowledge the reviewed snapshot and re-arm against the verified remote head right after the validated fix push, before waiting for CI.
- Report precisely: "No further actionable P1/P2 found in [scope] at [revision], with [limitations]." An incomplete scope is reported as incomplete.

## Ledger template

```markdown
# Review of <topic> (PR <number>)
Base / head / local changes:
Changed-file inventory and behaviour groups:
Open external findings:

| Group / versions | Producer → consumers inspected | Invariant / counterexample | Evidence and result | Coverage gap |
| --- | --- | --- | --- | --- |

| Finding / severity | Trigger and impact | Root cause / sibling paths | Regression or disconfirming evidence | State |
| --- | --- | --- | --- | --- |

Rounds and final challenge pass:
Checks (command, dataset, revision, result):
Remaining limitations / unresolved findings:
Local gate: complete or incomplete, with reason:
Push / CI / delivered artifact:
```
