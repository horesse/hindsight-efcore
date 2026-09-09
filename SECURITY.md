# Security

## Reporting a vulnerability

Use GitHub's private vulnerability reporting: **Security → Report a vulnerability** on this repository.
Do not open a public issue. You will get a response within a week.

## Scope

Hindsight generates SQL (history DDL, trigger functions, `set_config` calls) from EF Core model
metadata. Anything that lets model or user input reach generated SQL unquoted, or lets the change
context be spoofed across transactions, is in scope.

## Supported versions

Only the latest release line receives fixes. Pre-release versions (`-preview.N`) are supported until
the next pre-release.
