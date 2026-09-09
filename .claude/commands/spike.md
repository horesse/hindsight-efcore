You are about to work on an open question from `DESIGN.md → Open questions`: $ARGUMENTS

Before writing any code:

1. Quote the open question verbatim and the DESIGN.md decisions it depends on.
2. State the hypothesis you will test and the *single* integration test that would confirm it.
3. State the time box and the fallback named in DESIGN.md.
4. List the EF Core public APIs you intend to use. If any of them is in an `.Internal` namespace,
   stop here and report — do not proceed.

Wait for confirmation. After the spike, update the DESIGN.md entry (resolve the question, move the
decision up into a numbered entry) in the same change set. Spike code is allowed to be ugly; it is
not allowed to be untested.
