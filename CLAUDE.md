# Claude Code guidance

Claude Code follows the same project guidance as Codex. `AGENTS.md` is the single source of truth; keep instructions there rather than duplicating or diverging from them here. Only rules that apply to Claude Code alone are stated below.

@AGENTS.md

## Claude Code only

- Before making any commit, run an adversarial Codex review (the `codex-review` skill) of all the uncommitted changes. Fix the issues Codex finds, then repeat the full review of all the uncommitted changes. Commit only when a review comes back with no issues found (owner decision).
