---
name: review-compliance-classifier
description: Review Compliance Monitor classification changes or investigate incorrect results, focusing on requirement extraction, model requests, evidence policy, and offline regression coverage. Use for a requested classifier review or a classification defect, not routine formatting or unrelated API work.
---

# Review the compliance classifier

Review the current implementation against its documented policy and report reproducible findings. This workflow is a review, not authorization to change policy, adjust thresholds, or spend inference credits.

## Establish the baseline

Read repository instructions, Git status, and relevant staged/unstaged diffs. Inspect the current files rather than relying on previous review notes:

- `ComplianceMonitor.Api/Services/HuggingFaceComplianceClassifier.cs`
- `ComplianceMonitor.Api/Services/RequirementExtractor.cs`
- `ComplianceMonitor.Api/Services/IComplianceClassifier.cs`
- `ComplianceMonitor.Api/Models/ClassificationResult.cs`
- `ComplianceMonitor.Tests/ClassifierTests.cs`
- The classification policy and score explanation in `README.md`.

Read endpoint/error handling or other tests only when needed to trace a finding. If there is no classifier diff, review the current behavior and say so.

## Trace evidence through the decision

- Compare documented COMPLIES/DEVIATES/UNCLEAR meanings with the code. Keep model support, missing evidence, explicit contradiction, and final business policy distinct.
- Inspect extraction with simple and compound obligations, object lists, repeated requirements, negation, conditions, unsupported wording, and recurrence expressions. Check that obligations and modifiers survive extraction; do not assume splitting clauses establishes their meaning.
- Inspect the exact hypothesis strings, action-only premise, independent scoring settings, and duplicate-label handling. A mock can verify the request shape, but cannot establish whether BART interprets that wording correctly.
- Follow response validation and aggregation: unknown, missing, repeated, or malformed labels; invalid scores; weakest-requirement selection; threshold boundaries; deterministic overrides; and confidence semantics. Use different scores for different requirements when testing aggregation.
- Inspect cancellation and upstream failures if relevant. Verify that dependency failures do not become successful classifications or saved records.

## Verify without inference charges

Use the existing fake HTTP handler and isolated tests. Start with:

```sh
RUN_LIVE_ACCEPTANCE=0 dotnet test --filter FullyQualifiedName~ClassifierTests
```

Run additional offline tests only to resolve a specific concern. Prefer a minimal reproduction and counterexample for each suspected defect. Review by default without editing source or tests; create temporary probes outside the repository if needed. Fixes and persisted regression tests belong in an explicitly requested follow-up.

Never run the demo client, POST to a running API, load User Secrets, or enable live acceptance to verify a hypothesis during this review. If live evidence is necessary, explain the gap and propose a bounded call count for explicit approval; do not infer paid-call authorization from a review request.

## Deliver an evidence-based review

Present actionable findings first, ordered by impact. Each finding should include:

- A precise file/line reference and concrete trigger.
- Observed behavior and expected behavior under the documented policy.
- Evidence: source trace, actual offline command/test output, or an authoritative source when explaining external model/API behavior.
- The smallest reasonable correction and any policy decision requiring developer review.

Separate confirmed implementation defects, test gaps, and model limitations. State which tests ran and which checks were skipped; never present simulated scores as measured model accuracy. Do not manufacture findings to fill a quota. If no actionable defects are established, say so and describe the remaining verification limits. Provide proposed PROCESS.md notes only when requested; do not rewrite the developer's account.
