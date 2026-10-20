---
name: overview-skill-index
description: Use when choosing which repository skills to load for a task, mapping work to skill families, finding related skills, or updating skill routing after skills change.
last-updated: 2026-10-20
difficulty: basic
---

# Skill Cross-Reference Index

> Portable skill router. Read `.agent/repository-profile.md` first so profile-declared framework or repository overlays can refine generic routing.

## Selection Rules

1. Use existing source-owned guidance through the smallest set of skills that covers the task; do not create separate thin guidance forks.
2. Prefer generic `basic-*`, `overview-*`, and `test-*` skills for portable process guidance.
3. Load framework-specific skills only when the repository profile, source code, or user request names that framework.
4. Load profile-declared custom workflows and skills when they match the task.
5. If a referenced skill is missing after copying to another repository, skip it and rely on conventions/profile discovery.
6. Treat loader listings as a catalog. Load all skills only when broad context is needed.

## By Task

| Task | Skills to Load |
|------|----------------|
| Review a PR or diff | `basic-code-review` -> `basic-security-checklist` |
| Write documentation | `basic-documentation`; add `basic-documentation-diagrams` when diagrams are needed |
| Navigate repository | `overview-repository-structure` |
| Review architecture | repository source map -> `basic-code-review` |
| Modify CI/CD | `overview-github-actions` -> `basic-security-checklist` |
| Add unit tests | repository testing profile -> `test-unit`; add `drn-testing` when DRN test attributes or contexts apply |
| Review security | `basic-security-checklist` -> `basic-code-review` |
| Review commits or release conventions | `basic-git-conventions` |
| Work as an AI agent | `basic-agentic-development` |
| Pursue a goal through workflow routing | `/goal` workflow -> profile/custom route or skill overlays -> `basic-agentic-development` |
| Use repository-specific custom behavior | `.agent/repository-profile.md` -> matching `.agent/workflows/<custom>.md` or `.agent/skills/<custom>-*/SKILL.md` |
| Synchronize two sibling repositories/projects | `/sync` -> `basic-agentic-development` -> `basic-code-review` -> `basic-security-checklist` |
| Apply an approved sibling sync subscope | `/sync` -> `/sync-execute`, using `/sync-evidence` for the evidence protocol |
| Refresh local agent instructions/loaders from the current filesystem | `/update` workflow |
| Port `.agent` into a new repository | `/update all` -> full new-repository self-sync -> `/update` verification |

## Related Skills

- [overview-repository-structure.md](../overview-repository-structure/SKILL.md)
- [basic-code-review.md](../basic-code-review/SKILL.md)
