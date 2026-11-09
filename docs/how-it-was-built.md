# How HoardPad was built

HoardPad was built with [Claude Code](https://docs.anthropic.com/en/docs/claude-code)
by its maintainer, who set the direction and made the product decisions. The
work followed a fixed sequence of phases. Each phase produced a document that
the next phase treated as binding, and the riskiest parts were checked by
tests that were themselves tested. This page describes that process. The
documents it produced are published in
[`design-record/`](design-record/).

## Why it was worth building

The goal was a small, fast, Notepad++-style editor with Markdown preview, a
checklist file type, and an AI assistant that can work on your files. The
hard part is the last item. An AI agent that can write to disk needs a limit
that does not depend on the model behaving well. That one requirement shaped
the build process: the safety controls had to be provable by tests, and the
tests had to be able to fail.

## The phases

### 1. Brief

A short seed brief fixed the product identity and the constraints: C# on
Avalonia, Linux and Windows, no web-stack editor, no telemetry, and an AI
panel with a hard safety boundary.

### 2. Requirements (PRD), with hostile review

The [requirements document](design-record/prd.md) turned the brief into
numbered functional requirements, non-functional requirements, and pass/fail
success criteria. Reviewers acted as adversaries: their job was to find
blockers, ambiguities, and unmeasurable claims. The PRD went through
**five hostile-review rounds**. Each round's findings were fixed in the
document or recorded as an explicit decision, and no requirement ID was ever
renumbered, so later documents could cite them reliably.

### 3. Technical design, with spikes

Where the PRD depended on a fact it could not yet state, the technical design
phase ran a **spike**: a small throwaway experiment that measured the fact.
There were five spikes and two smaller sub-spikes. They measured cold-start
time with and without ReadyToRun compilation, picked the Markdown renderer,
verified the exact `claude -p` launch flags and hook protocol against a real
CLI version, measured the per-turn cap values, and tested the durability of
the file formats. The
[technical design](design-record/tech-design.md) pinned every provisional
number to a measured value. Things that could not be measured on the build
machine, mainly the Windows runtime behavior, were recorded as **deferred**,
not claimed as met.

### 4. Architecture, with specialist review

The [architecture document](design-record/architecture.md) turned the design
into a project layout, a dependency rule, a threading model, and a package
manifest. A group of specialist reviewers checked it, and their findings
were folded into the document. The rules that matter most, such as "the
domain project has no platform dependency", were turned into build errors
and architecture tests rather than left as conventions.

### 5. User-facing design

The [design document](design-record/design.md) described the screens,
keyboard shortcuts, states, and error messages, and mapped each Notepad++
feature to match, adapt, or drop for version 1. Specialists reviewed it too.
A late product decision (nested checklists) reopened the requirements and
design, and the change was made in place with the same discipline: amended
documents, no renumbering, and an explicit re-sign-off.

### 6. Brand

A color palette for the dark and light themes was defined as a fixed set of
tokens. A script checks the text and background pairs against the WCAG AA
contrast ratio, and the application binds its colors to those tokens. The
brand assets are composited from the original artwork, not regenerated.

### 7. Phased build with review gates

The application was built in phases, from the platform-free domain kernel up
to the Avalonia surfaces. Each unit of work had a machine-checkable
verification, such as a build and a test run, and was not marked done until it
passed. Code was reviewed by independent reviewers against the specification,
and then for quality, before it was accepted.

## How the safety-critical parts were gated

### A separate protected set

The AI safety spine was treated as a **protected set** of files: the sandbox
policy, the launch-safety check, the cap-state store, the hook script, and the
agent launcher. Changes to them required a security review and a rerun of the
acceptance gate. The build tracked whether these files were byte-identical to
the last accepted version.

### A deterministic acceptance gate

A live language model is not a reliable test: it might never attempt an
escape. So the central acceptance gate does not use one. It drives the
production hook script as a subprocess with crafted tool inputs: a create, an
edit, a deliberate out-of-sandbox write, and a destructive overwrite. It then
asserts the hook's decision and the resulting file, activity-log entry, and
undo-journal entry. A second gate drives the shared file-operation service
with the operations the default backend cannot reach. The gate checks the
service's journal and log records. In the shipped 1.0.0 app, live agent writes
do not produce those records; only the replay path does (see the README's
Known limitations).

### Mutation-tested gates

A gate that cannot fail proves nothing. For the safety gate, the build
deliberately broke a safety property, for example by removing the sandbox
root from the environment or flipping a decision, and confirmed that a gate
turned red. A gate that stayed green under a deliberate break was treated as
vacuous and fixed.

### Review rounds that found real defects

Independent review rounds caught problems that the tests had passed.
Examples from the build record: a byte-order-mark handling path that could
corrupt a UTF-32 file, tab drag-and-drop that did nothing in the running app
while its headless tests passed because a required property was unset, and
an AI-confirmation dialog whose focus logic was inverted. The review process
had a fixed bar: only real breaks blocked a milestone, and a review was
capped at a small number of rounds before it escalated to the maintainer.

### Live verification on an isolated display

Unit tests cannot close a user-interface item. Every change to a visible
surface was verified by running a **fresh published build** on a dedicated
virtual display with its own window manager, driving it with synthetic
keyboard and mouse input, and capturing a screenshot as evidence. The build
used a wrapper script that guaranteed the virtual display was torn down
afterward, and it never touched the maintainer's real desktop. This habit came
from experience: several early "fixed" claims had been accepted from green
unit tests and then failed in the running application. The test
discipline that came out of it includes a positive control for any synthetic
input, because some input paths silently do nothing.

### Honest deferrals

What the build could not verify, it said so. The Windows runtime legs of the
safety hook, the process-tree kill, the first-write durability path, and
Windows performance are carried as deferred in the design record, and the
[README](../README.md) states the platform status plainly.

## A privacy gate for the public release

The public copy of this repository was produced from the private working tree
by a scripted export. The export removes personal information, machine paths,
and internal planning documents, and a scan runs in CI for generic shapes (Gmail addresses, `/home/<user>`
paths, and server-mount paths). Checks for specific private values run locally
before each commit, not in CI. The design record published here went through the same
process.

## What this shows

- **Documents as contracts.** Each phase's output was binding on the next,
  and changes were amended in place instead of rewritten.
- **Measure, then decide.** Spikes replaced guesses with numbers before the
  design committed to them.
- **A test must be able to fail.** The safety gate was mutation-tested.
- **Live proof for visible changes.** Passing unit tests never closed a UI
  item.
- **Say what was not verified.** Deferred items stayed deferred in the
  record.
