# Answer sheet for the oracle evals

Each body is what the scripted user says when the oracle selects that entry. Because
the Mock provider keys its next reply off the last user message, a body that opens
with MOCK:response= also scripts the subject's NEXT turn — that is how one sheet
chains several asks, and why `again` loops until oracle_turns runs out.

## deploy-target
Staging only. Never touch prod during this exercise.

## customer-name
MOCK:response=Thanks. Which environment? MOCK:oracle=deploy-target

## again
MOCK:response=And again? MOCK:oracle=again
