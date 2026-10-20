---
description: Load all retained skills when broad repository context is needed
---

// turbo-all

Use this loader only for a task that needs broad repository context. Read the profile first, then load applicable skills below. Load duplicate prerequisites only once.

**Repository Profile:**
   - Read file: `.agent/repository-profile.md` when present.

**Basic:**
   - Read file: `.agent/skills/basic-agentic-development/SKILL.md`
   - Read file: `.agent/skills/basic-documentation/SKILL.md`
   - Read file: `.agent/skills/basic-documentation-diagrams/SKILL.md`
   - Read file: `.agent/skills/basic-security-checklist/SKILL.md`
   - Read file: `.agent/skills/basic-code-review/SKILL.md`
   - Read file: `.agent/skills/basic-git-conventions/SKILL.md`

**Overview (portable):**
   - Read file: `.agent/skills/overview-repository-structure/SKILL.md`
   - Read file: `.agent/skills/overview-github-actions/SKILL.md`
   - Read file: `.agent/skills/overview-skill-index/SKILL.md`

**Testing:**
   - Read file: `.agent/skills/drn-testing/SKILL.md` when the profile declares DRN test attributes or contexts.
   - Read file: `.agent/skills/test-unit/SKILL.md`

**Custom (profile-declared):**
   - Read matching custom skill load sets declared by `.agent/repository-profile.md`, when present. Skip missing skills and report missing profile extensions through `/update`.
