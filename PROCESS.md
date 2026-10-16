# Development process

I used several coding agents throughout this exercise: one for project setup and boilerplate, another to explore the initial classification approach, and reviewers to challenge the implementation as it developed. I also worked with agents to refine the algorithm after Case 3 failed. I directed the work, reviewed proposed changes, and decided which behavior and tradeoffs to accept.

## 1. Context, constraints, and instructions

I started with the exercise requirements: a C#/.NET API using `facebook/bart-large-mnli`, persistent analysis results, history and summary endpoints, a simple client, and tests. I shared the supplied acceptance cases and used them as an initial check of the implementation.

As the project developed, I provided the existing code, Git history, manual API results, and specific failures as context. I asked agents to inspect the repository before editing, preserve my changes, and explain proposed algorithm changes before implementing them. I wanted decisions supported by code, observed behavior, tests, or documentation rather than an unsupported recommendation.

I set the following constraints and captured them in the repository's agent instructions:

- Build on the existing .NET Minimal API, EF Core SQLite, and classifier interface. Favor readable, testable code and avoid unnecessary infrastructure.
- Keep model evidence separate from the application's compliance policy. Do not hardcode the supplied examples to make the tests pass.
- Protect credentials, use offline tests by default, and obtain bounded approval before spending inference credits. Do not automatically retry paid requests.
- Handle invalid inputs and dependency failures explicitly. Persist completed analyses only.
- Leave changes for my review. I managed commits rather than allowing agents to commit automatically.

I kept the exercise's scope in mind. My goal was a bounded implementation with explicit limitations, rather than a general-purpose natural-language rules engine.

## 2. What I delegated and what I handled myself

I delegated boilerplate, implementation work, test implementation, the console client, and documentation drafts. I used separate reviews to find gaps and challenge assumptions. This let me spend more of my attention on the classification policy, failure behavior, and whether the result satisfied the exercise.

I handled direction, acceptance decisions, manual checks, code and documentation review, and targeted edits, including edits to the classifier. Much of the implementation was agent-generated; my contribution was to evaluate it, identify where it fell short, and direct corrections.

My main decisions were:

- **Define what counts as compliance.** I required evidence for every applicable mandatory requirement. Under this strict verification policy, an unsupported requirement produces DEVIATES, although missing evidence does not prove a real-world violation. This distinction mattered when interpreting Case 3.
- **Understand the algorithm before accepting changes.** I asked how NLI, hypothesis wording, and model scores contributed to the result. I challenged examples involving habitual behavior and conditional obligations to clarify where the policy and implementation needed boundaries.
- **Make the client useful beyond the examples.** I requested an interactive default and a separate demo mode so users could submit their own inputs and inspect history or summary without automatically invoking inference.
- **Require meaningful verification.** I challenged tests that covered only broad success paths and requested malformed-response, cancellation, boundary, mixed-score, and recovery cases. I used reviewer findings to direct follow-up work.
- **Control scope and presentation.** I reviewed the README for clear setup instructions and useful organization, requested a small architecture diagram, and chose to document remaining classifier limitations instead of continuing to expand the parser.

## 3. What went wrong and how I addressed it

### Case 3 exposed an incomplete classification approach

The initial approach returned COMPLIES for “Rebooted the server and checked logs” against “Servers must be rebooted weekly and logs reviewed after restart.” I identified that the report did not establish the required weekly recurrence. A plausible overall match between the sentences was insufficient.

I asked for a general correction before implementation and rejected a fix specific to the word “weekly.” The resulting approach evaluated individual requirements and added a bounded recurrence check. I reviewed the policy interpretation and required regression coverage. The recurrence check remains a wording-based guard with documented limitations.

### A threshold change broke a valid example

During refinement, a proposed 0.80 cutoff caused the valid confirmation-email example to return DEVIATES. In the approved live run, its score was approximately 0.675; the other supplied cases matched their expected results.

I used that failure to guide the correction: the cutoff was restored to 0.55, and the observed scores became offline regression inputs. The run used three paid inference requests because the no-guideline case was handled locally. Restoring the cutoff fixed that regression, but did not establish that the threshold was calibrated or that model accuracy had been validated generally.

### Early tests left important gaps

The initial tests did not adequately cover extraction, duplicate labels, or requirements with different scores. Review found that a fake HTTP handler gave every requirement the same score, which could conceal an aggregation defect. I requested mixed scores, reordered responses, and threshold boundaries.

Further testing exposed incorrect timeout handling, charset handling failures, and ignored unsupported content encoding. I directed corrections and regression coverage, including checks that a later request could succeed after an upstream failure and that failed classifications were not persisted.

### Generated client and documentation drafts needed revision

The first client only submitted fixed examples, and its error messages did not clearly distinguish connection failures from invalid responses. I requested interactive input, a separate demo mode, and clearer diagnostics.

I also revised the direction of the README drafts: clarify prerequisites, separate API setup from client usage and testing, and remove unnecessary detail. I treated generated documentation as something to review and edit, just like generated code.

## Verification and remaining limitations

I used manual API checks, offline automated tests, reviewer feedback, and a bounded live inference run. These provided different evidence: simulated responses verified application rules and failure handling, while live requests exposed model behavior on specific examples. Passing offline tests does not establish semantic model accuracy.

The README documents the remaining limits in requirement extraction, conditional applicability, recurrence interpretation, and score calibration. I chose to make those limits explicit so the submission communicates both what the implementation does and where further validation would be needed.
