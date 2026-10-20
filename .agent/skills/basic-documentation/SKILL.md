---
name: basic-documentation
description: "Concise, source-backed repository documentation. Keywords: documentation, readme, markdown, release-notes"
last-updated: 2026-10-20
difficulty: basic
---

# Documentation

Standards for creating and maintaining repository documentation.

## 1. Core Principles (DiSCOS)

- **Security First**: Document security implications, auth requirements, sensitive data handling.
- **Clarity**: Reader's context first. Define terms on first use.
- **Conciseness**: Lead with the useful fact or action. Use short sentences and natural language. Omit filler and repeated summaries. Do not use em dashes or en dashes.
- **Accuracy**: Verify claims against source. State uncertainty when evidence is incomplete.
- **Maintenance**: Documentation is code. Update with every significant change.
- **Shared Defaults**: Cross-package defaults and docs-sync rules belong in the repository profile or the owning source documentation. Package READMEs must stay self-contained for important facts and may reference sibling package READMEs for related guidance.

## 2. README.md Standard

### Structure

Preserve the repository's existing structure and badges. Include purpose, prerequisites, setup and relevant operational or security guidance. Link to detailed guides instead of repeating them. Add summaries, feature lists or navigation only when they help the reader.

### Best Practices
- Use `[ ]`/`[x]` checklists for feature status
- Include minimal working examples
- Mention security considerations upfront

## 3. ROADMAP vs CHANGELOG vs RELEASE-NOTES

### ROADMAP.md

Track planned features with `[ ]`/`[/]`/`[x]` markers, grouped by version.

### CHANGELOG.md

Repository/service-level history. Reverse chronological. Sections: Added, Changed, Fixed, Security, Breaking.

### RELEASE-NOTES.md

Per-package/module release notes. Use the repository profile or package metadata for the exact template, heading style, sections, prefix, and footer. Maintain versioned changes above existing history.

Update release notes when a change affects a published module's consumers or package metadata other than version-only alignment:
- Public API, contract, CLI, endpoint, event, DTO, configuration key, default, security posture, operational behavior, data/migration behavior, or observable bug fix.
- Dependency, runtime, container, or build-output changes only when they are breaking, security-relevant, consumer-visible, or alter published package artifacts.
- Published docs that are shipped as package metadata, such as package READMEs or release notes.

Do not add release-note entries for internal-only refactors, tests, comments, private/internal-only checks, agent-only docs, or routine dependency-only updates unless the repository profile declares a stricter rule.

Skip private/internal-only checks unless needed to prove public behavior or docs accuracy.

For version-aligned releases, do not add artificial version blocks to unchanged packages unless the repository profile explicitly requires them; record release notes as not required.

### SemVer

- **Major (X.0.0)**: Breaking changes
- **Minor (0.X.0)**: New features (backward compatible)
- **Patch (0.0.X)**: Bug fixes and security patches

## 4. Agent Skills Documentation

Path: `.agent/skills/<skill-name>/SKILL.md`

### Frontmatter (YAML)
```yaml
---
name: skill-name
description: "Concise description (<100 chars) with keywords. Keywords: keyword1, keyword2"
last-updated: YYYY-MM-DD
difficulty: basic | intermediate | advanced
---
```

| Field | Required | Purpose |
|-------|----------|---------|
| `name` | Yes | Skill identifier (matches directory name) |
| `description` | Yes | Concise summary with `Keywords:` suffix for semantic matching |
| `last-updated` | Yes | Date of last meaningful content change |
| `difficulty` | Yes | Triage hint: **basic** (orientation/reference), **intermediate** (domain context needed), **advanced** (deep framework knowledge) |

### Content Sections
1. **Title**: `# Skill Name`
2. **Overview**: Purpose and scope paragraph
3. **Table of Contents**: Optional when it helps navigation
4. **Sections**: Logical breakdown with tables, code blocks, examples
5. **Code Blocks**: Runnable snippets with language specification

## 5. Security Documentation

Mandatory for components handling sensitive data, auth, or external communication.

Cover applicable threats, authorization, data protection and operational safeguards. Link to existing policies and reporting instructions. Add separate sections only when the content needs them.

## 6. API Documentation

### REST API Pattern
```markdown
### POST /api/users
Creates a new user account.
**Authentication**: Required (Bearer token)
**Request/Response**: JSON with status codes (201, 400, 401, 409)
```

### Code API Pattern
Use XML documentation comments (`<summary>`, `<param>`, `<returns>`, `<exception>`).

## 7. Markdown Standards

- **Headers**: ATX style (`#`), one `#` per document
- **Lists**: Hyphens (`-`) unordered, `1.` ordered
- **Links**: Relative paths for internal files
- **Code**: Always specify language
- **Diagrams**: Use when a visual clarifies the explanation. See [basic-documentation-diagrams](../basic-documentation-diagrams/SKILL.md) for styling.
- **Tables**: Use for structured data, align columns
- **TOC**: Add when it helps navigation, not based on line count alone

## 8. Code Block Best Practices

- Always specify language (```csharp, ```json, ```bash)
- Include necessary imports/usings
- Code should work as-is or clearly indicate placeholders
- Explain non-obvious logic with comments

## 9. Verification Checklist

- [ ] Security implications documented?
- [ ] Scannable in 30 seconds?
- [ ] Code snippets checked against source? Execution limits stated?
- [ ] All public APIs documented?
- [ ] Alt text for images? Semantic structure?
- [ ] Diagrams meet WCAG AA? (see [diagrams skill](../basic-documentation-diagrams/SKILL.md))
- [ ] Relative links valid?
- [ ] Repeated summaries and unnecessary sections removed?
- [ ] Concrete examples for complex concepts?
